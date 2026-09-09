using NAudio.Dsp;
using NAudio.Wave.SampleProviders;
using VoidClip.Models;

namespace VoidClip.Audio;

// ══════════════════════════════════════════════════════════════
//  CHANNEL PLUMBING
//  Effects are written against planar mono buffers; the app's
//  canonical format is interleaved stereo, so everything passes
//  through here on the way in and out.
// ══════════════════════════════════════════════════════════════
internal static class Planar
{
    public static float[][] Split(float[] interleaved)
    {
        var frames = interleaved.Length / Fmt.Channels;
        var res = new float[Fmt.Channels][];
        for (int c = 0; c < Fmt.Channels; c++)
        {
            res[c] = new float[frames];
            for (int f = 0; f < frames; f++) res[c][f] = interleaved[f * Fmt.Channels + c];
        }
        return res;
    }

    public static float[] Join(float[][] planar)
    {
        var frames = planar[0].Length;
        var res = new float[frames * Fmt.Channels];
        for (int c = 0; c < Fmt.Channels; c++)
        {
            var src = planar[c];
            for (int f = 0; f < frames; f++) res[f * Fmt.Channels + c] = f < src.Length ? src[f] : 0f;
        }
        return res;
    }

    /// <summary>Mono sum, used by anything that has to analyse rather than colour.</summary>
    public static float[] Mono(float[] interleaved)
    {
        var frames = interleaved.Length / Fmt.Channels;
        var res = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float s = 0;
            for (int c = 0; c < Fmt.Channels; c++) s += interleaved[f * Fmt.Channels + c];
            res[f] = s / Fmt.Channels;
        }
        return res;
    }
}

// ══════════════════════════════════════════════════════════════
//  PHASE VOCODER
//
//  One STFT pass that can shift pitch, move the spectral envelope
//  (formants) independently of it, and rewrite phase outright for
//  the robot and whisper effects. Auto-tune drives it by handing
//  it a different pitch ratio for every frame.
// ══════════════════════════════════════════════════════════════
public sealed class Spectral
{
    public const int FftSize = 2048;
    public const int Osamp = 4;
    public const int Hop = FftSize / Osamp;

    private const int Half = FftSize / 2;
    private const int Bits = 11;              // log2(FftSize)
    private const int EnvelopeWidth = 12;     // ± bins averaged for the formant envelope

    /// <summary>Fixed pitch ratio, used when <see cref="RatioPerFrame"/> is null.</summary>
    public double Ratio { get; set; } = 1.0;

    /// <summary>One pitch ratio per STFT frame — how auto-tune bends a performance.</summary>
    public double[] RatioPerFrame { get; set; }

    /// <summary>Extra multiplier on the spectral envelope, on top of whatever the pitch does to it.</summary>
    public double FormantRatio { get; set; } = 1.0;

    /// <summary>Hold the envelope still while the pitch moves. Off gives the chipmunk sound.</summary>
    public bool PreserveFormants { get; set; } = true;

    /// <summary>0 normal · 1 zero phase (robot) · 2 random phase (whisper).</summary>
    public int PhaseMode { get; set; }

    public static int FrameCount(int samples) => Math.Max(1, (samples + Hop - 1) / Hop);

    public float[] Process(float[] mono)
    {
        if (mono.Length == 0) return mono;

        var win = new double[FftSize];
        for (int k = 0; k < FftSize; k++) win[k] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * k / FftSize);

        var outAcc = new double[mono.Length + FftSize];
        var winAcc = new double[mono.Length + FftSize];

        var lastPhase = new double[Half + 1];
        var sumPhase = new double[Half + 1];
        var anaMag = new double[Half + 1];
        var anaFreq = new double[Half + 1];
        var synMag = new double[Half + 1];
        var synFreq = new double[Half + 1];
        var env = new double[Half + 1];
        var prefix = new double[Half + 2];
        var fft = new Complex[FftSize];
        var rng = new Random(1234);

        var expected = 2.0 * Math.PI * Hop / FftSize;
        var freqPerBin = (double)Fmt.Rate / FftSize;

        int frame = 0;
        for (int pos = 0; pos < mono.Length; pos += Hop, frame++)
        {
            for (int k = 0; k < FftSize; k++)
            {
                var idx = pos + k;
                fft[k].X = idx < mono.Length ? (float)(mono[idx] * win[k]) : 0f;
                fft[k].Y = 0f;
            }
            FastFourierTransform.FFT(true, Bits, fft);

            // ── analysis: magnitude and true frequency per bin ──
            for (int k = 0; k <= Half; k++)
            {
                double re = fft[k].X, im = fft[k].Y;
                anaMag[k] = Math.Sqrt(re * re + im * im);
                var phase = Math.Atan2(im, re);

                var delta = phase - lastPhase[k];
                lastPhase[k] = phase;
                delta -= k * expected;

                var wraps = (int)(delta / Math.PI);
                if (wraps >= 0) wraps += wraps & 1; else wraps -= wraps & 1;
                delta -= Math.PI * wraps;
                delta = Osamp * delta / (2.0 * Math.PI);

                anaFreq[k] = (k + delta) * freqPerBin;
            }

            BuildEnvelope(anaMag, prefix, env);

            var ratio = RatioPerFrame != null
                ? RatioPerFrame[Math.Min(frame, RatioPerFrame.Length - 1)]
                : Ratio;
            ratio = Math.Clamp(ratio, 0.25, 4.0);

            // Where the envelope ends up: held still when formants are preserved,
            // dragged along with the pitch when they are not.
            var envShift = (PreserveFormants ? 1.0 : ratio) * FormantRatio;

            Array.Clear(synMag);
            Array.Clear(synFreq);

            for (int k = 0; k <= Half; k++)
            {
                var target = (int)Math.Round(k * ratio);
                if (target < 0 || target > Half) continue;
                var residual = anaMag[k] / Math.Max(env[k], 1e-10);
                synMag[target] += residual * EnvAt(env, target / envShift);
                synFreq[target] = anaFreq[k] * ratio;
            }

            // ── synthesis ──
            for (int k = 0; k <= Half; k++)
            {
                var delta = synFreq[k] / freqPerBin - k;
                delta = 2.0 * Math.PI * delta / Osamp;
                delta += k * expected;
                sumPhase[k] += delta;

                var phase = PhaseMode switch
                {
                    1 => 0.0,
                    2 => rng.NextDouble() * 2.0 * Math.PI,
                    _ => sumPhase[k]
                };

                fft[k].X = (float)(synMag[k] * Math.Cos(phase));
                fft[k].Y = (float)(synMag[k] * Math.Sin(phase));
            }
            fft[0].Y = 0f;
            fft[Half].Y = 0f;
            for (int k = Half + 1; k < FftSize; k++)
            {
                fft[k].X = fft[FftSize - k].X;
                fft[k].Y = -fft[FftSize - k].Y;
            }

            FastFourierTransform.FFT(false, Bits, fft);

            for (int k = 0; k < FftSize; k++)
            {
                outAcc[pos + k] += win[k] * fft[k].X;
                winAcc[pos + k] += win[k] * win[k];
            }
        }

        // Dividing by the accumulated window energy makes the overlap-add exact,
        // including at both ends where fewer frames overlap.
        var res = new float[mono.Length];
        for (int i = 0; i < res.Length; i++)
        {
            var w = winAcc[i];
            res[i] = w > 1e-6 ? (float)Math.Clamp(outAcc[i] / w, -8.0, 8.0) : 0f;
        }
        return res;
    }

    /// <summary>Smoothed magnitude spectrum — a cheap stand-in for the vocal tract's response.</summary>
    private static void BuildEnvelope(double[] mag, double[] prefix, double[] env)
    {
        prefix[0] = 0;
        for (int k = 0; k <= Half; k++) prefix[k + 1] = prefix[k] + mag[k];
        for (int k = 0; k <= Half; k++)
        {
            var lo = Math.Max(0, k - EnvelopeWidth);
            var hi = Math.Min(Half, k + EnvelopeWidth);
            env[k] = (prefix[hi + 1] - prefix[lo]) / (hi - lo + 1);
        }
    }

    private static double EnvAt(double[] env, double x)
    {
        if (x <= 0) return env[0];
        if (x >= Half) return env[Half];
        var i = (int)x;
        var f = x - i;
        return env[i] * (1 - f) + env[Math.Min(Half, i + 1)] * f;
    }
}

// ══════════════════════════════════════════════════════════════
//  PITCH TRACKING (YIN)
//
//  Runs on a 12 kHz decimation of the mono sum — a voice never
//  needs more than that, and it makes the search ~16× cheaper.
// ══════════════════════════════════════════════════════════════
public static class PitchTrack
{
    public const int Rate = 12000;
    private const int Decim = Fmt.Rate / Rate;      // 4
    private const int Window = 512;
    private const int TauMin = 11;                  // ~1090 Hz
    private const int TauMax = 185;                 // ~65 Hz

    /// <summary>
    /// One pitch estimate per STFT frame. <paramref name="confidence"/> is 0..1;
    /// anything unvoiced comes back as 0 Hz.
    /// </summary>
    public static void Detect(float[] mono48, int frames, out double[] hz, out double[] confidence)
    {
        hz = new double[frames];
        confidence = new double[frames];

        var small = Decimate(mono48);
        var hopSmall = Spectral.Hop / Decim;
        var diff = new double[TauMax + 2];
        var cmnd = new double[TauMax + 2];

        for (int f = 0; f < frames; f++)
        {
            var start = f * hopSmall;
            if (start + Window + TauMax >= small.Length) break;

            double energy = 0;
            for (int j = 0; j < Window; j++) energy += small[start + j] * small[start + j];
            if (energy / Window < 1e-7) continue;      // silence: leave it unvoiced

            for (int tau = 1; tau <= TauMax; tau++)
            {
                double sum = 0;
                for (int j = 0; j < Window; j++)
                {
                    var d = small[start + j] - small[start + j + tau];
                    sum += d * d;
                }
                diff[tau] = sum;
            }

            // cumulative mean normalised difference — the trick that makes YIN work
            cmnd[0] = 1;
            double running = 0;
            for (int tau = 1; tau <= TauMax; tau++)
            {
                running += diff[tau];
                cmnd[tau] = running > 1e-12 ? diff[tau] * tau / running : 1;
            }

            var best = -1;
            for (int tau = TauMin; tau < TauMax; tau++)
            {
                if (cmnd[tau] < 0.15 && cmnd[tau] <= cmnd[tau + 1]) { best = tau; break; }
            }
            if (best < 0)
            {
                var lowest = double.MaxValue;
                for (int tau = TauMin; tau <= TauMax; tau++)
                    if (cmnd[tau] < lowest) { lowest = cmnd[tau]; best = tau; }
                if (best < 0 || lowest > 0.6) continue;
            }

            // parabolic interpolation for sub-sample period accuracy
            var refined = (double)best;
            if (best > 1 && best < TauMax)
            {
                var a = cmnd[best - 1];
                var b = cmnd[best];
                var c = cmnd[best + 1];
                var denom = 2 * (2 * b - a - c);
                if (Math.Abs(denom) > 1e-12) refined = best + (c - a) / denom;
            }

            hz[f] = Rate / Math.Max(1e-6, refined);
            confidence[f] = Math.Clamp(1 - cmnd[best], 0, 1);
        }
    }

    private static float[] Decimate(float[] mono48)
    {
        var res = new float[mono48.Length / Decim + 1];
        for (int i = 0; i < res.Length; i++)
        {
            float sum = 0;
            int n = 0;
            for (int j = 0; j < Decim; j++)
            {
                var idx = i * Decim + j;
                if (idx >= mono48.Length) break;
                sum += mono48[idx];
                n++;
            }
            res[i] = n > 0 ? sum / n : 0f;
        }
        return res;
    }
}

// ══════════════════════════════════════════════════════════════
//  THE PROCESSOR — one method per effect, all offline
// ══════════════════════════════════════════════════════════════
public static class FxProcessor
{
    /// <summary>Runs a whole chain. Length can change: speed, reverse and the tails all move it.</summary>
    public static float[] Run(float[] stereo, IEnumerable<FxUnit> units)
    {
        if (stereo == null || stereo.Length == 0 || units == null) return stereo ?? Array.Empty<float>();
        var audio = stereo;
        foreach (var u in units)
        {
            if (u == null || !u.Enabled) continue;
            try { audio = Apply(audio, u); }
            catch (Exception ex) { Services.AppPaths.Log($"FX {u.Type} failed: {ex.Message}"); }
        }
        return audio;
    }

    public static float[] Apply(float[] stereo, FxUnit u) => u.Type switch
    {
        FxType.Pitch => Pitch(stereo, u),
        FxType.AutoTune => AutoTune(stereo, u),
        FxType.Formant => Formant(stereo, u),
        FxType.Harmonizer => Harmonizer(stereo, u),
        FxType.Speed => Speed(stereo, u),
        FxType.Eq => Eq(stereo, u),
        FxType.Filter => Filter(stereo, u),
        FxType.Distortion => Distortion(stereo, u),
        FxType.BitCrush => BitCrush(stereo, u),
        FxType.Chorus => Chorus(stereo, u),
        FxType.Flanger => Flanger(stereo, u),
        FxType.Phaser => Phaser(stereo, u),
        FxType.Vibrato => Vibrato(stereo, u),
        FxType.Tremolo => Tremolo(stereo, u),
        FxType.RingMod => RingMod(stereo, u),
        FxType.Delay => Delay(stereo, u),
        FxType.Reverb => Reverb(stereo, u),
        FxType.Compressor => Compressor(stereo, u),
        FxType.Gate => Gate(stereo, u),
        FxType.Robot => Respell(stereo, u, 1),
        FxType.Whisper => Respell(stereo, u, 2),
        FxType.Width => Width(stereo, u),
        FxType.Reverse => Reverse(stereo),
        FxType.Gain => Gain(stereo, u),
        _ => stereo
    };

    // ──────────────────────────────────────────────────────────
    //  SHARED HELPERS
    // ──────────────────────────────────────────────────────────
    private static float[] Blend(float[] dry, float[] wet, double mix)
    {
        if (mix >= 0.999) return wet;
        var n = Math.Max(dry.Length, wet.Length);
        var res = new float[n];
        var d = 1 - mix;
        for (int i = 0; i < n; i++)
        {
            var a = i < dry.Length ? dry[i] : 0f;
            var b = i < wet.Length ? wet[i] : 0f;
            res[i] = (float)(a * d + b * mix);
        }
        return res;
    }

    private static float[] Pad(float[] stereo, double seconds)
    {
        var extra = (int)(seconds * Fmt.Rate) * Fmt.Channels;
        if (extra <= 0) return stereo;
        var res = new float[stereo.Length + extra];
        Array.Copy(stereo, res, stereo.Length);
        return res;
    }

    /// <summary>Cuts a decayed tail back to where it stops mattering.</summary>
    private static float[] TrimTail(float[] stereo, int keepAtLeast)
    {
        const float floor = 0.00016f;   // ≈ −76 dBFS
        var last = stereo.Length - 1;
        while (last > keepAtLeast && Math.Abs(stereo[last]) < floor) last--;
        var len = Math.Min(stereo.Length, ((last / Fmt.Channels) + 1) * Fmt.Channels);
        if (len >= stereo.Length) return stereo;
        var res = new float[len];
        Array.Copy(stereo, res, len);
        return res;
    }

    private static double Db(double db) => Math.Pow(10, db / 20.0);

    /// <summary>
    /// Puts a spectrally-processed buffer back at the level it arrived at.
    /// Shifting a voice down piles several bins onto one, and flattening phase
    /// stacks every partial on top of itself — both come out far too loud
    /// without this, and the mix would just clip them.
    /// </summary>
    private static float[] MatchLevel(float[] dry, float[] wet)
    {
        if (dry.Length == 0 || wet.Length == 0) return wet;

        var dryRms = Dsp.Rms(dry, 0, dry.Length);
        var wetRms = Dsp.Rms(wet, 0, wet.Length);
        if (dryRms < 1e-6f || wetRms < 1e-6f) return wet;

        var gain = Math.Clamp(dryRms / wetRms, 0.1f, 8f);
        var ceiling = Math.Max(Dsp.Peak(dry), 0.99f);
        var peak = Dsp.Peak(wet) * gain;
        if (peak > ceiling) gain *= ceiling / peak;

        Dsp.ApplyGain(wet, gain);
        return wet;
    }

    private static float[] EachChannel(float[] stereo, Func<float[], int, float[]> process)
    {
        var planar = Planar.Split(stereo);
        for (int c = 0; c < planar.Length; c++) planar[c] = process(planar[c], c);
        return Planar.Join(planar);
    }

    /// <summary>Speed change by resampling — the tape-machine kind, pitch included.</summary>
    public static float[] Resample(float[] stereo, double rate)
    {
        if (stereo.Length == 0 || Math.Abs(rate - 1) < 1e-6) return stereo;
        var srcRate = (int)Math.Round(Fmt.Rate * Math.Clamp(rate, 0.25, 4.0));
        srcRate = Math.Clamp(srcRate, 12000, 192000);

        var src = new ArraySampleProvider(stereo, srcRate, Fmt.Channels);
        var rs = new WdlResamplingSampleProvider(src, Fmt.Rate);
        var outLen = (int)((long)stereo.Length * Fmt.Rate / srcRate) + 8192;
        var buf = new float[outLen];
        int total = 0, read;
        while (total < buf.Length && (read = rs.Read(buf, total, Math.Min(16384, buf.Length - total))) > 0)
            total += read;
        total -= total % Fmt.Channels;
        var res = new float[total];
        Array.Copy(buf, res, total);
        return res;
    }

    /// <summary>Pitch shift that leaves the length alone.</summary>
    public static float[] PitchShift(float[] stereo, double semitones, double formantSemitones, bool preserve)
    {
        if (stereo.Length == 0) return stereo;
        if (Math.Abs(semitones) < 0.001 && Math.Abs(formantSemitones) < 0.001) return stereo;

        var ratio = Math.Pow(2, semitones / 12.0);
        var formant = Math.Pow(2, formantSemitones / 12.0);
        return MatchLevel(stereo, EachChannel(stereo, (data, _) => new Spectral
        {
            Ratio = ratio,
            FormantRatio = formant,
            PreserveFormants = preserve
        }.Process(data)));
    }

    // ──────────────────────────────────────────────────────────
    //  VOICE
    // ──────────────────────────────────────────────────────────
    private static float[] Pitch(float[] stereo, FxUnit u)
    {
        var semis = u.Get("semitones") + u.Get("cents") / 100.0;
        var wet = PitchShift(stereo, semis, u.Get("formant"), u.On("preserve"));
        return Blend(stereo, wet, u.Get("mix") / 100.0);
    }

    private static float[] Formant(float[] stereo, FxUnit u)
    {
        var wet = PitchShift(stereo, 0, u.Get("shift"), true);
        return Blend(stereo, wet, u.Get("mix") / 100.0);
    }

    private static float[] Respell(float[] stereo, FxUnit u, int phaseMode)
    {
        var formant = Math.Pow(2, u.Get("formant") / 12.0);
        var wet = MatchLevel(stereo, EachChannel(stereo, (data, _) => new Spectral
        {
            Ratio = 1,
            FormantRatio = formant,
            PreserveFormants = true,
            PhaseMode = phaseMode
        }.Process(data)));
        return Blend(stereo, wet, u.Get("mix") / 100.0);
    }

    private static float[] AutoTune(float[] stereo, FxUnit u)
    {
        if (stereo.Length == 0) return stereo;

        var mono = Planar.Mono(stereo);
        var frames = Spectral.FrameCount(mono.Length);
        PitchTrack.Detect(mono, frames, out var hz, out var conf);

        var mask = Music.Mask(u.Choice("key"), u.Choice("scale"));
        var strength = Math.Clamp(u.Get("strength") / 100.0, 0, 1);
        var transpose = u.Get("transpose");
        var vibratoCents = u.Get("vibrato");
        var vibratoRate = u.Get("vibratoRate");
        var minConf = 0.25 + (1 - Math.Clamp(u.Get("sensitivity") / 100.0, 0, 1)) * 0.5;

        var hopSeconds = (double)Spectral.Hop / Fmt.Rate;
        var retune = u.Get("retune") / 1000.0;
        var alpha = retune <= 0.0005 ? 1.0 : 1 - Math.Exp(-hopSeconds / retune);

        var ratios = new double[frames];
        double correction = 0;      // semitones currently being applied, smoothed

        for (int f = 0; f < frames; f++)
        {
            double target = 0;
            if (hz[f] > 0 && conf[f] >= minConf)
            {
                var midi = Music.HzToMidi(hz[f]);
                var snapped = Music.Snap(midi, mask);
                target = (snapped - midi) * strength;
                // a runaway estimate (an octave error) would sound far worse than doing nothing
                if (Math.Abs(target) > 6) target = 0;
            }
            correction += (target - correction) * alpha;

            var vibrato = vibratoCents > 0
                ? Math.Sin(2 * Math.PI * vibratoRate * f * hopSeconds) * vibratoCents / 100.0
                : 0;

            ratios[f] = Math.Pow(2, (correction + transpose + vibrato) / 12.0);
        }

        var preserve = u.On("preserve");
        var wet = MatchLevel(stereo, EachChannel(stereo, (data, _) => new Spectral
        {
            RatioPerFrame = ratios,
            PreserveFormants = preserve
        }.Process(data)));

        return Blend(stereo, wet, u.Get("mix") / 100.0);
    }

    private static float[] Harmonizer(float[] stereo, FxUnit u)
    {
        var preserve = u.On("preserve");
        var detune = u.Get("detune") / 100.0;
        var spread = Math.Clamp(u.Get("spread") / 100.0, 0, 1);
        var dry = u.Get("dry") / 100.0;

        var res = new float[stereo.Length];
        for (int i = 0; i < res.Length; i++) res[i] = (float)(stereo[i] * dry);

        var voices = new[]
        {
            (semis: u.Get("v1"), level: u.Get("v1level") / 100.0, pan: -1.0),
            (semis: u.Get("v2"), level: u.Get("v2level") / 100.0, pan:  1.0),
            (semis: u.Get("v3"), level: u.Get("v3level") / 100.0, pan:  0.0),
        };

        for (int v = 0; v < voices.Length; v++)
        {
            var (semis, level, pan) = voices[v];
            if (level <= 0.001) continue;

            var shifted = PitchShift(stereo, semis + (v % 2 == 0 ? detune : -detune), 0, preserve);
            var lGain = level * (1 - Math.Max(0, pan) * spread * 0.85);
            var rGain = level * (1 - Math.Max(0, -pan) * spread * 0.85);

            var n = Math.Min(res.Length, shifted.Length);
            for (int i = 0; i + 1 < n; i += 2)
            {
                res[i] += (float)(shifted[i] * lGain);
                res[i + 1] += (float)(shifted[i + 1] * rGain);
            }
        }

        // deliberately not clamped to ±1 — the mixdown scales the finished
        // arrangement down as a whole, which sounds better than clipping here
        for (int i = 0; i < res.Length; i++) res[i] = Math.Clamp(res[i], -8f, 8f);
        return res;
    }

    private static float[] Speed(float[] stereo, FxUnit u)
    {
        var rate = Math.Clamp(u.Get("rate"), 0.25, 4.0);
        if (Math.Abs(rate - 1) < 1e-4) return stereo;

        var resampled = Resample(stereo, rate);
        if (!u.On("keepPitch")) return resampled;

        // Resampling moved the pitch by `rate`; put it back and only the length has changed.
        var semis = -12 * Math.Log2(rate);
        return PitchShift(resampled, semis, 0, true);
    }

    // ──────────────────────────────────────────────────────────
    //  TONE
    // ──────────────────────────────────────────────────────────
    private static float[] Eq(float[] stereo, FxUnit u)
    {
        var lowGain = u.Get("lowGain");
        var midGain = u.Get("midGain");
        var highGain = u.Get("highGain");
        if (Math.Abs(lowGain) < 0.01 && Math.Abs(midGain) < 0.01 && Math.Abs(highGain) < 0.01) return stereo;

        return EachChannel(stereo, (data, _) =>
        {
            var low = BiQuadFilter.LowShelf(Fmt.Rate, (float)u.Get("lowFreq"), 0.7f, (float)lowGain);
            var mid = BiQuadFilter.PeakingEQ(Fmt.Rate, (float)u.Get("midFreq"), (float)u.Get("midQ"), (float)midGain);
            var high = BiQuadFilter.HighShelf(Fmt.Rate, (float)u.Get("highFreq"), 0.7f, (float)highGain);
            var res = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
                res[i] = high.Transform(mid.Transform(low.Transform(data[i])));
            return res;
        });
    }

    private static float[] Filter(float[] stereo, FxUnit u)
    {
        var type = u.Choice("type");
        var cutoff = (float)Math.Clamp(u.Get("cutoff"), 20, Fmt.Rate / 2.2);
        var q = (float)u.Get("res");

        var wet = EachChannel(stereo, (data, _) =>
        {
            var f = type switch
            {
                1 => BiQuadFilter.HighPassFilter(Fmt.Rate, cutoff, q),
                2 => BiQuadFilter.BandPassFilterConstantPeakGain(Fmt.Rate, cutoff, q),
                3 => BiQuadFilter.NotchFilter(Fmt.Rate, cutoff, q),
                _ => BiQuadFilter.LowPassFilter(Fmt.Rate, cutoff, q)
            };
            var res = new float[data.Length];
            for (int i = 0; i < data.Length; i++) res[i] = f.Transform(data[i]);
            return res;
        });
        return Blend(stereo, wet, u.Get("mix") / 100.0);
    }

    private static float[] Width(float[] stereo, FxUnit u)
    {
        var width = u.Get("width") / 100.0;
        var pan = Math.Clamp(u.Get("pan") / 100.0, -1, 1);
        var res = new float[stereo.Length];

        // equal-power pan so the middle does not sound louder than the edges
        var angle = (pan + 1) * Math.PI / 4;
        var lGain = Math.Cos(angle) * Math.Sqrt(2);
        var rGain = Math.Sin(angle) * Math.Sqrt(2);

        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            var mid = (stereo[i] + stereo[i + 1]) * 0.5;
            var side = (stereo[i] - stereo[i + 1]) * 0.5 * width;
            res[i] = (float)Math.Clamp((mid + side) * lGain, -8, 8);
            res[i + 1] = (float)Math.Clamp((mid - side) * rGain, -8, 8);
        }
        return res;
    }

    // ──────────────────────────────────────────────────────────
    //  CHARACTER
    // ──────────────────────────────────────────────────────────
    private static float[] Distortion(float[] stereo, FxUnit u)
    {
        var type = u.Choice("type");
        var drive = Db(u.Get("drive"));
        var outGain = Db(u.Get("out"));
        var tone = (float)Math.Clamp(u.Get("tone"), 200, Fmt.Rate / 2.2);

        var wet = EachChannel(stereo, (data, _) =>
        {
            var lp = BiQuadFilter.LowPassFilter(Fmt.Rate, tone, 0.7f);
            var res = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                var x = data[i] * drive;
                double y = type switch
                {
                    1 => Math.Clamp(x, -0.7, 0.7) / 0.7,
                    2 => Math.Sign(x) * (1 - Math.Exp(-Math.Abs(x) * 2.2)),
                    3 => Fold(x),
                    _ => Math.Tanh(x)
                };
                res[i] = lp.Transform((float)(y * outGain));
            }
            return res;
        });
        return Blend(stereo, wet, u.Get("mix") / 100.0);
    }

    private static double Fold(double x)
    {
        // reflect anything past ±1 back into range, repeatedly
        for (int i = 0; i < 8 && Math.Abs(x) > 1; i++) x = Math.Sign(x) * (2 - Math.Abs(x));
        return Math.Clamp(x, -1, 1);
    }

    private static float[] BitCrush(float[] stereo, FxUnit u)
    {
        var bits = Math.Clamp(u.Get("bits"), 1, 16);
        var levels = Math.Pow(2, bits) - 1;
        var down = Math.Max(1, (int)Math.Round(u.Get("down")));

        var wet = new float[stereo.Length];
        var held = new float[Fmt.Channels];
        var frames = stereo.Length / Fmt.Channels;

        for (int f = 0; f < frames; f++)
        {
            var sample = f % down == 0;
            for (int c = 0; c < Fmt.Channels; c++)
            {
                var i = f * Fmt.Channels + c;
                if (sample)
                {
                    var q = Math.Round((stereo[i] * 0.5 + 0.5) * levels) / levels;
                    held[c] = (float)((q - 0.5) * 2);
                }
                wet[i] = held[c];
            }
        }
        return Blend(stereo, wet, u.Get("mix") / 100.0);
    }

    private static float[] RingMod(float[] stereo, FxUnit u)
    {
        var freq = u.Get("freq");
        var wet = new float[stereo.Length];
        var frames = stereo.Length / Fmt.Channels;
        for (int f = 0; f < frames; f++)
        {
            var m = Math.Sin(2 * Math.PI * freq * f / Fmt.Rate);
            for (int c = 0; c < Fmt.Channels; c++)
            {
                var i = f * Fmt.Channels + c;
                wet[i] = (float)(stereo[i] * m);
            }
        }
        return Blend(stereo, wet, u.Get("mix") / 100.0);
    }

    private static float[] Tremolo(float[] stereo, FxUnit u)
    {
        var rate = u.Get("rate");
        var depth = Math.Clamp(u.Get("depth") / 100.0, 0, 1);
        var shape = u.Choice("shape");
        var stereoPhase = u.Get("stereo") / 100.0 * Math.PI;

        var res = new float[stereo.Length];
        var frames = stereo.Length / Fmt.Channels;
        for (int f = 0; f < frames; f++)
        {
            var phase = 2 * Math.PI * rate * f / Fmt.Rate;
            for (int c = 0; c < Fmt.Channels; c++)
            {
                var p = phase + (c == 1 ? stereoPhase : 0);
                var lfo = shape switch
                {
                    1 => 1 - 2 * Math.Abs(((p / (2 * Math.PI)) % 1) * 2 - 1),
                    2 => Math.Sin(p) >= 0 ? 1.0 : -1.0,
                    _ => Math.Sin(p)
                };
                var gain = 1 - depth * (0.5 - 0.5 * lfo);
                var i = f * Fmt.Channels + c;
                res[i] = (float)(stereo[i] * gain);
            }
        }
        return res;
    }

    /// <summary>A delay line read at a fractional offset — the guts of chorus, flanger and vibrato.</summary>
    private static float ReadDelay(float[] buf, int writeIdx, double delaySamples)
    {
        var read = writeIdx - delaySamples;
        while (read < 0) read += buf.Length;
        var i0 = (int)read;
        var frac = read - i0;
        var i1 = (i0 + 1) % buf.Length;
        return (float)(buf[i0 % buf.Length] * (1 - frac) + buf[i1] * frac);
    }

    private static float[] Chorus(float[] stereo, FxUnit u)
    {
        var rate = u.Get("rate");
        var depthMs = u.Get("depth");
        var voices = Math.Clamp((int)Math.Round(u.Get("voices")), 1, 4);
        var spread = Math.Clamp(u.Get("spread") / 100.0, 0, 1);

        var baseMs = 18.0;
        var size = (int)((baseMs + depthMs + 5) * Fmt.Rate / 1000) + 4;
        var planar = Planar.Split(stereo);
        var wet = new float[planar.Length][];

        for (int c = 0; c < planar.Length; c++)
        {
            var data = planar[c];
            var buf = new float[size];
            var outp = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                buf[i % size] = data[i];
                double sum = 0;
                for (int v = 0; v < voices; v++)
                {
                    var phase = 2 * Math.PI * (rate * i / Fmt.Rate)
                              + v * 2 * Math.PI / voices
                              + (c == 1 ? spread * Math.PI : 0);
                    var delayMs = baseMs + depthMs * 0.5 * (1 + Math.Sin(phase));
                    sum += ReadDelay(buf, i, delayMs * Fmt.Rate / 1000);
                }
                outp[i] = (float)(sum / voices);
            }
            wet[c] = outp;
        }
        return Blend(stereo, Planar.Join(wet), u.Get("mix") / 100.0);
    }

    private static float[] Flanger(float[] stereo, FxUnit u)
    {
        var rate = u.Get("rate");
        var depthMs = u.Get("depth");
        var feedback = Math.Clamp(u.Get("feedback") / 100.0, -0.95, 0.95);

        var size = (int)((depthMs + 6) * Fmt.Rate / 1000) + 4;
        var planar = Planar.Split(stereo);
        var wet = new float[planar.Length][];

        for (int c = 0; c < planar.Length; c++)
        {
            var data = planar[c];
            var buf = new float[size];
            var outp = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                var phase = 2 * Math.PI * (rate * i / Fmt.Rate) + (c == 1 ? Math.PI / 2 : 0);
                var delayMs = 0.6 + depthMs * 0.5 * (1 + Math.Sin(phase));
                var delayed = ReadDelay(buf, i, delayMs * Fmt.Rate / 1000);
                buf[i % size] = (float)Math.Clamp(data[i] + delayed * feedback, -3, 3);
                outp[i] = delayed;
            }
            wet[c] = outp;
        }
        return Blend(stereo, Planar.Join(wet), u.Get("mix") / 100.0);
    }

    private static float[] Vibrato(float[] stereo, FxUnit u)
    {
        var rate = u.Get("rate");
        var depthMs = Math.Clamp(u.Get("depth") / 100.0, 0, 1) * 6;
        if (depthMs < 0.01) return stereo;

        var size = (int)((depthMs * 2 + 10) * Fmt.Rate / 1000) + 4;
        var planar = Planar.Split(stereo);
        for (int c = 0; c < planar.Length; c++)
        {
            var data = planar[c];
            var buf = new float[size];
            var outp = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                buf[i % size] = data[i];
                var delayMs = depthMs + depthMs * Math.Sin(2 * Math.PI * rate * i / Fmt.Rate);
                outp[i] = ReadDelay(buf, i, (delayMs + 1) * Fmt.Rate / 1000);
            }
            planar[c] = outp;
        }
        return Planar.Join(planar);
    }

    private static float[] Phaser(float[] stereo, FxUnit u)
    {
        var rate = u.Get("rate");
        var depth = Math.Clamp(u.Get("depth") / 100.0, 0, 1);
        var stages = (u.Choice("stages") + 1) * 2;
        var feedback = Math.Clamp(u.Get("feedback") / 100.0, -0.95, 0.95);

        var planar = Planar.Split(stereo);
        var wet = new float[planar.Length][];

        for (int c = 0; c < planar.Length; c++)
        {
            var data = planar[c];
            var zx = new float[stages];
            var zy = new float[stages];
            var outp = new float[data.Length];
            float last = 0;

            for (int i = 0; i < data.Length; i++)
            {
                var phase = 2 * Math.PI * rate * i / Fmt.Rate + (c == 1 ? Math.PI / 2 : 0);
                var sweep = 300 + depth * 2200 * (0.5 + 0.5 * Math.Sin(phase));
                var a = (float)((1 - Math.Tan(Math.PI * sweep / Fmt.Rate)) / (1 + Math.Tan(Math.PI * sweep / Fmt.Rate)));

                var x = (float)Math.Clamp(data[i] + last * feedback, -3, 3);
                for (int s = 0; s < stages; s++)
                {
                    var y = -a * x + zx[s] + a * zy[s];
                    zx[s] = x; zy[s] = y; x = y;
                }
                last = x;
                outp[i] = x;
            }
            wet[c] = outp;
        }
        return Blend(stereo, Planar.Join(wet), u.Get("mix") / 100.0);
    }

    // ──────────────────────────────────────────────────────────
    //  SPACE
    // ──────────────────────────────────────────────────────────
    private static float[] Delay(float[] stereo, FxUnit u)
    {
        var timeMs = Math.Clamp(u.Get("time"), 5, 2000);
        var feedback = Math.Clamp(u.Get("feedback") / 100.0, 0, 0.95);
        var damping = (float)Math.Clamp(u.Get("damping"), 500, Fmt.Rate / 2.2);
        var pingpong = u.On("pingpong");
        var mix = u.Get("mix") / 100.0;
        if (mix <= 0.001) return stereo;

        // leave room for the repeats to die away
        var repeats = feedback <= 0.01 ? 1 : Math.Min(24, (int)Math.Ceiling(Math.Log(0.001) / Math.Log(feedback)));
        var padded = Pad(stereo, Math.Min(8.0, timeMs / 1000.0 * repeats + 0.2));

        var delaySamples = (int)(timeMs * Fmt.Rate / 1000);
        var planar = Planar.Split(padded);
        var frames = planar[0].Length;

        var bufL = new float[delaySamples + 1];
        var bufR = new float[delaySamples + 1];
        var dampL = BiQuadFilter.LowPassFilter(Fmt.Rate, damping, 0.7f);
        var dampR = BiQuadFilter.LowPassFilter(Fmt.Rate, damping, 0.7f);

        var wetL = new float[frames];
        var wetR = new float[frames];

        for (int i = 0; i < frames; i++)
        {
            var idx = i % bufL.Length;
            var outL = bufL[idx];
            var outR = bufR[idx];

            var fbL = dampL.Transform(outL) * (float)feedback;
            var fbR = dampR.Transform(outR) * (float)feedback;

            if (pingpong)
            {
                bufL[idx] = (float)Math.Clamp(planar[0][i] + fbR, -3, 3);
                bufR[idx] = (float)Math.Clamp(planar[1][i] + fbL, -3, 3);
            }
            else
            {
                bufL[idx] = (float)Math.Clamp(planar[0][i] + fbL, -3, 3);
                bufR[idx] = (float)Math.Clamp(planar[1][i] + fbR, -3, 3);
            }

            wetL[i] = outL;
            wetR[i] = outR;
        }

        var wet = Planar.Join(new[] { wetL, wetR });
        return TrimTail(Blend(padded, wet, mix), stereo.Length);
    }

    // Freeverb, at 48 kHz. The tunings are the original ones scaled off 44.1 kHz.
    private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllpassTuning = { 556, 441, 341, 225 };
    private const int StereoSpread = 23;

    private static float[] Reverb(float[] stereo, FxUnit u)
    {
        var mix = u.Get("mix") / 100.0;
        if (mix <= 0.001) return stereo;

        var size = Math.Clamp(u.Get("size") / 100.0, 0, 1);
        var damp = Math.Clamp(u.Get("damping") / 100.0, 0, 1);
        var width = Math.Clamp(u.Get("width") / 100.0, 0, 1);
        var predelay = (int)(u.Get("predelay") * Fmt.Rate / 1000);

        var roomSize = 0.7 + size * 0.28;         // 0.70 … 0.98
        var damping = 0.2 + damp * 0.6;

        var padded = Pad(stereo, 0.8 + size * 3.0);
        var planar = Planar.Split(padded);
        var frames = planar[0].Length;

        var scale = Fmt.Rate / 44100.0;
        var combs = new Comb[2][];
        var allpasses = new Allpass[2][];
        for (int c = 0; c < 2; c++)
        {
            combs[c] = new Comb[CombTuning.Length];
            for (int i = 0; i < CombTuning.Length; i++)
                combs[c][i] = new Comb((int)((CombTuning[i] + (c == 1 ? StereoSpread : 0)) * scale), roomSize, damping);
            allpasses[c] = new Allpass[AllpassTuning.Length];
            for (int i = 0; i < AllpassTuning.Length; i++)
                allpasses[c][i] = new Allpass((int)((AllpassTuning[i] + (c == 1 ? StereoSpread : 0)) * scale), 0.5);
        }

        var wetL = new float[frames];
        var wetR = new float[frames];

        for (int i = 0; i < frames; i++)
        {
            var src = i - predelay;
            var input = src >= 0 ? (planar[0][src] + planar[1][src]) * 0.5f * 0.35f : 0f;

            for (int c = 0; c < 2; c++)
            {
                float sum = 0;
                foreach (var comb in combs[c]) sum += comb.Process(input);
                foreach (var ap in allpasses[c]) sum = ap.Process(sum);
                if (c == 0) wetL[i] = sum; else wetR[i] = sum;
            }
        }

        // width: fold the two tails together by however much narrowing was asked for
        var narrow = (1 - width) * 0.5;
        for (int i = 0; i < frames; i++)
        {
            var l = wetL[i]; var r = wetR[i];
            wetL[i] = (float)(l * (1 - narrow) + r * narrow);
            wetR[i] = (float)(r * (1 - narrow) + l * narrow);
        }

        var wet = Planar.Join(new[] { wetL, wetR });
        return TrimTail(Blend(padded, wet, mix), stereo.Length);
    }

    private sealed class Comb
    {
        private readonly float[] _buf;
        private int _pos;
        private float _store;
        private readonly float _feedback;
        private readonly float _damp;

        public Comb(int size, double feedback, double damping)
        {
            _buf = new float[Math.Max(4, size)];
            _feedback = (float)feedback;
            _damp = (float)damping;
        }

        public float Process(float input)
        {
            var output = _buf[_pos];
            _store = output * (1 - _damp) + _store * _damp;
            _buf[_pos] = Math.Clamp(input + _store * _feedback, -3f, 3f);
            if (++_pos >= _buf.Length) _pos = 0;
            return output;
        }
    }

    private sealed class Allpass
    {
        private readonly float[] _buf;
        private int _pos;
        private readonly float _feedback;

        public Allpass(int size, double feedback)
        {
            _buf = new float[Math.Max(4, size)];
            _feedback = (float)feedback;
        }

        public float Process(float input)
        {
            var buffered = _buf[_pos];
            var output = -input + buffered;
            _buf[_pos] = Math.Clamp(input + buffered * _feedback, -3f, 3f);
            if (++_pos >= _buf.Length) _pos = 0;
            return output;
        }
    }

    // ──────────────────────────────────────────────────────────
    //  DYNAMICS
    // ──────────────────────────────────────────────────────────
    private static float[] Compressor(float[] stereo, FxUnit u)
    {
        var threshold = u.Get("threshold");
        var ratio = Math.Max(1, u.Get("ratio"));
        var knee = u.Get("knee");
        var makeup = Db(u.Get("makeup"));

        var attack = Math.Exp(-1.0 / (Fmt.Rate * u.Get("attack") / 1000.0));
        var release = Math.Exp(-1.0 / (Fmt.Rate * u.Get("release") / 1000.0));

        var res = new float[stereo.Length];
        var frames = stereo.Length / Fmt.Channels;
        double envelope = 0;

        for (int f = 0; f < frames; f++)
        {
            double peak = 0;
            for (int c = 0; c < Fmt.Channels; c++) peak = Math.Max(peak, Math.Abs(stereo[f * Fmt.Channels + c]));

            var coeff = peak > envelope ? attack : release;
            envelope = peak + (envelope - peak) * coeff;

            var levelDb = 20 * Math.Log10(Math.Max(envelope, 1e-9));
            var over = levelDb - threshold;

            double reduction = 0;
            if (knee > 0 && over > -knee / 2 && over < knee / 2)
            {
                var t = over + knee / 2;
                reduction = (1 - 1 / ratio) * t * t / (2 * knee);
            }
            else if (over >= knee / 2)
            {
                reduction = over * (1 - 1 / ratio);
            }

            var gain = Db(-reduction) * makeup;
            for (int c = 0; c < Fmt.Channels; c++)
            {
                var i = f * Fmt.Channels + c;
                res[i] = (float)Math.Clamp(stereo[i] * gain, -8, 8);
            }
        }
        return res;
    }

    private static float[] Gate(float[] stereo, FxUnit u)
    {
        var threshold = Db(u.Get("threshold"));
        var floor = Db(u.Get("range"));
        var holdSamples = (int)(u.Get("hold") * Fmt.Rate / 1000);
        var attack = 1.0 / Math.Max(1, u.Get("attack") * Fmt.Rate / 1000);
        var release = 1.0 / Math.Max(1, u.Get("release") * Fmt.Rate / 1000);

        var res = new float[stereo.Length];
        var frames = stereo.Length / Fmt.Channels;
        double gain = floor;
        double envelope = 0;
        int held = 0;

        for (int f = 0; f < frames; f++)
        {
            double peak = 0;
            for (int c = 0; c < Fmt.Channels; c++) peak = Math.Max(peak, Math.Abs(stereo[f * Fmt.Channels + c]));
            envelope = Math.Max(peak, envelope * 0.9995);

            if (envelope > threshold) { held = holdSamples; gain = Math.Min(1, gain + attack); }
            else if (held > 0) { held--; gain = Math.Min(1, gain + attack); }
            else gain = Math.Max(floor, gain - release);

            for (int c = 0; c < Fmt.Channels; c++)
            {
                var i = f * Fmt.Channels + c;
                res[i] = (float)(stereo[i] * gain);
            }
        }
        return res;
    }

    private static float[] Gain(float[] stereo, FxUnit u)
    {
        var res = (float[])stereo.Clone();
        var ceiling = Db(u.Get("ceiling"));

        if (u.On("normalise")) Dsp.Normalize(res, u.Get("ceiling"));
        Dsp.ApplyGain(res, (float)Db(u.Get("gain")));

        var peak = Dsp.Peak(res);
        if (peak > ceiling && peak > 0) Dsp.ApplyGain(res, (float)(ceiling / peak));
        return res;
    }

    private static float[] Reverse(float[] stereo)
    {
        var res = new float[stereo.Length];
        var frames = stereo.Length / Fmt.Channels;
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < Fmt.Channels; c++)
                res[f * Fmt.Channels + c] = stereo[(frames - 1 - f) * Fmt.Channels + c];
        return res;
    }
}
