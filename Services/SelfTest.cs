using System.IO;
using System.Text;
using NAudio.CoreAudioApi;
using VoidClip.Audio;

namespace VoidClip.Services;

/// <summary>
/// Exercises the whole audio path — DSP, WAV round-trip, MP3 and OGG encoders,
/// device enumeration — so a broken setup reports itself instead of failing silently.
/// </summary>
public static class SelfTest
{
    public static string Run()
    {
        var sb = new StringBuilder();
        var pass = 0;
        var fail = 0;

        void Check(string name, Func<string> test)
        {
            try
            {
                var detail = test();
                sb.AppendLine($"  PASS  {name}{(string.IsNullOrEmpty(detail) ? "" : "  —  " + detail)}");
                pass++;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  FAIL  {name}  —  {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        }

        sb.AppendLine("VoidClip self-test  \u2014  " + UpdateService.BuildDescription);
        sb.AppendLine("────────────────────────");

        var dir = Path.Combine(Path.GetTempPath(), "VoidClipSelfTest");
        Directory.CreateDirectory(dir);

        // ── a 3 second stereo tone with a silent head and tail ──
        var tone = MakeTone(3.0);

        Check("Signal generation", () =>
        {
            if (tone.Length != Fmt.Rate * Fmt.Channels * 3) throw new Exception("wrong sample count");
            return $"{tone.Length:N0} samples, peak {Dsp.Peak(tone):0.00}";
        });

        Check("Normalise", () =>
        {
            var copy = (float[])tone.Clone();
            Dsp.Normalize(copy, -1.5);
            var peak = Dsp.Peak(copy);
            var target = (float)Math.Pow(10, -1.5 / 20.0);
            if (Math.Abs(peak - target) > 0.02f) throw new Exception($"peak landed at {peak:0.000}, wanted {target:0.000}");
            return $"peak {peak:0.000} (−1.5 dBFS)";
        });

        Check("Fades", () =>
        {
            var copy = (float[])tone.Clone();
            Dsp.Fade(copy, Fmt.Channels, 100, 100, Fmt.Rate);
            if (Math.Abs(copy[0]) > 0.001f) throw new Exception("start not faded");
            if (Math.Abs(copy[^1]) > 0.001f) throw new Exception("end not faded");
            return "in/out ramps applied";
        });

        Check("Silence trim", () =>
        {
            var copy = (float[])tone.Clone();
            var trimmed = Dsp.TrimSilence(copy, Fmt.Channels, Fmt.Rate, -45);
            if (trimmed.Length >= copy.Length) throw new Exception("nothing was trimmed");
            return $"{WavIO.Duration(copy):0.00}s → {WavIO.Duration(trimmed):0.00}s";
        });

        Check("Peak extraction", () =>
        {
            var peaks = Dsp.Peaks(tone, Fmt.Channels, 72);
            if (peaks.Length != 72) throw new Exception("wrong bucket count");
            if (peaks.Max() <= 0) throw new Exception("all buckets empty");
            return "72 buckets";
        });

        Check("Resample 44.1k → 48k", () =>
        {
            var src = new float[44100 * 2];
            for (int i = 0; i < src.Length; i++) src[i] = (float)Math.Sin(i * 0.01);
            var outp = Dsp.ToCanonical(src, 44100, 2);
            var seconds = WavIO.Duration(outp);
            if (Math.Abs(seconds - 1.0) > 0.05) throw new Exception($"length drifted to {seconds:0.000}s");
            return $"1.000s → {seconds:0.000}s";
        });

        Check("Mono → stereo fold", () =>
        {
            var mono = new float[48000];
            for (int i = 0; i < mono.Length; i++) mono[i] = 0.5f;
            var outp = Dsp.ToCanonical(mono, 48000, 1);
            if (outp.Length != mono.Length * 2) throw new Exception("channel count wrong");
            return "ok";
        });

        // ── file formats ──
        var wav = Path.Combine(dir, "test.wav");
        Check("WAV write", () =>
        {
            WavIO.Write(wav, tone);
            var len = new FileInfo(wav).Length;
            if (len < 1000) throw new Exception("file too small");
            return $"{len / 1024.0:0.#} KB";
        });

        Check("WAV read-back", () =>
        {
            var back = WavIO.Read(wav);
            var diff = Math.Abs(WavIO.Duration(back) - WavIO.Duration(tone));
            if (diff > 0.01) throw new Exception($"duration drifted {diff:0.000}s");
            return $"{WavIO.Duration(back):0.00}s round-tripped";
        });

        var mp3 = Path.Combine(dir, "test.mp3");
        Check("MP3 encode (LAME)", () =>
        {
            Exporter.ToMp3(mp3, tone, 192);
            var bytes = File.ReadAllBytes(mp3);
            if (bytes.Length < 2000) throw new Exception("file too small");
            var isId3 = bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3';
            var isSync = bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0;
            if (!isId3 && !isSync) throw new Exception("no MP3 frame header found");
            return $"{bytes.Length / 1024.0:0.#} KB, {(isId3 ? "ID3 tagged" : "raw frames")}";
        });

        var ogg = Path.Combine(dir, "test.ogg");
        Check("OGG encode (Vorbis)", () =>
        {
            Exporter.ToOgg(ogg, tone, 0.5f);
            var bytes = File.ReadAllBytes(ogg);
            if (bytes.Length < 2000) throw new Exception("file too small");
            if (bytes[0] != 'O' || bytes[1] != 'g' || bytes[2] != 'g' || bytes[3] != 'S')
                throw new Exception("missing OggS capture pattern");
            var hasVorbis = Encoding.ASCII.GetString(bytes, 0, Math.Min(200, bytes.Length)).Contains("vorbis");
            if (!hasVorbis) throw new Exception("no vorbis identification header");
            return $"{bytes.Length / 1024.0:0.#} KB, OggS + vorbis headers";
        });

        // Windows has no built-in Vorbis decoder, so verify the container structurally:
        // walking every page and reading the final granule position proves the stream
        // is complete and the right length.
        Check("OGG stream structure", () =>
        {
            var (pages, granule) = WalkOggPages(File.ReadAllBytes(ogg));
            if (pages < 4) throw new Exception($"only {pages} pages — stream looks truncated");
            var seconds = granule / (double)Fmt.Rate;
            if (Math.Abs(seconds - 3.0) > 0.15) throw new Exception($"final granule says {seconds:0.00}s, expected 3.00s");
            return $"{pages} pages, {seconds:0.00}s of samples";
        });

        Check("MP3 decodes back", () =>
        {
            var back = WavIO.Read(mp3);
            if (back.Length == 0) throw new Exception("decoded to nothing");
            return $"{WavIO.Duration(back):0.00}s decoded";
        });

        // ── devices ──
        Check("Output devices", () =>
        {
            var d = AudioDevices.Render();
            if (d.Count <= 1) throw new Exception("no active playback devices found");
            return $"{d.Count - 1} found";
        });

        Check("Input devices", () =>
        {
            var d = AudioDevices.Capture();
            return $"{Math.Max(0, d.Count - 1)} found";
        });

        Check("Loopback device opens", () =>
        {
            var dev = AudioDevices.Get(Core.Settings?.LoopbackDeviceId ?? "", DataFlow.Render);
            using var cap = new NAudio.Wave.WasapiLoopbackCapture(dev);
            var wf = cap.WaveFormat;
            return $"{dev.FriendlyName} · {wf.SampleRate / 1000.0:0.#} kHz · {wf.Channels} ch";
        });

        // Plays a short quiet beep and loopback-records the same endpoint, which is the
        // only way to prove the output path really carries audio.
        Check("Playback reaches the output device", () =>
        {
            var probe = new Models.AppSettings
            {
                OutputDeviceId = Core.Settings?.OutputDeviceId ?? "",
                MonitorEnabled = false,
                MasterVolume = 0.8,
                PlaybackLatencyMs = 60
            };

            using var engine = new PlaybackEngine();
            string problem = null;
            engine.Problem += m => problem ??= m;
            engine.Configure(probe);
            if (problem != null) throw new Exception(problem);

            var dev = AudioDevices.Get(probe.OutputDeviceId, DataFlow.Render);
            using var cap = new NAudio.Wave.WasapiLoopbackCapture(dev);

            float peak = 0;
            cap.DataAvailable += (s, e) =>
            {
                var bits = cap.WaveFormat.BitsPerSample;
                if (bits == 32)
                    for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
                    {
                        var v = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                        if (v > peak) peak = v;
                    }
                else if (bits == 16)
                    for (int i = 0; i + 2 <= e.BytesRecorded; i += 2)
                    {
                        var v = Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                        if (v > peak) peak = v;
                    }
            };

            cap.StartRecording();
            Thread.Sleep(250);
            engine.Play("selftest", Beep(0.6, 0.3f), 1f, false);
            Thread.Sleep(1100);
            cap.StopRecording();
            Thread.Sleep(150);

            if (peak < 0.005f)
                throw new Exception($"nothing came back from the endpoint (peak {peak:0.0000})");
            return $"{dev.FriendlyName} · captured peak {peak:0.000}";
        });

        // ── the editor's DSP: pitch tracking, shifting, tuning, and a mixdown ──
        Check("Pitch tracking", () =>
        {
            var voice = MakeVoice(220, 1.2);
            var hz = MedianPitch(voice);
            if (Math.Abs(hz - 220) > 8) throw new Exception($"read a 220 Hz tone as {hz:0.0} Hz");
            return $"220 Hz tone read as {hz:0.0} Hz";
        });

        Check("Pitch shift", () =>
        {
            var voice = MakeVoice(220, 1.2);
            var up = FxProcessor.PitchShift(voice, 7, 0, true);
            if (up.Length != voice.Length) throw new Exception("the length changed");
            var hz = MedianPitch(up);
            var want = 220 * Math.Pow(2, 7 / 12.0);
            if (Math.Abs(hz - want) > 16) throw new Exception($"+7 st landed on {hz:0.0} Hz, wanted {want:0.0} Hz");
            if (Dsp.Peak(up) > 1.001f) throw new Exception($"came back over 0 dBFS ({Dsp.Peak(up):0.00})");
            return $"+7 st: 220 Hz → {hz:0.0} Hz, peak {Dsp.Peak(up):0.00}";
        });

        Check("Auto-tune", () =>
        {
            var offKey = MakeVoice(240, 1.2);      // sits between A#3 and B3
            var unit = new Models.FxUnit(FxType.AutoTune);
            unit.Put("scale", 0);                  // chromatic
            unit.Put("strength", 100);
            unit.Put("retune", 0);
            var tuned = FxProcessor.Apply(offKey, unit);
            var midi = Music.HzToMidi(MedianPitch(tuned));
            var cents = Math.Abs(midi - Math.Round(midi)) * 100;
            if (cents > 30) throw new Exception($"output sat {cents:0} cents off the nearest note");
            return $"240 Hz pulled onto {Music.NoteName(midi)}, {cents:0} cents off";
        });

        Check("Effects", () =>
        {
            var voice = MakeVoice(220, 0.8);
            var slow = new List<string>();
            foreach (var info in FxCatalog.All)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var outp = FxProcessor.Apply(voice, new Models.FxUnit(info.Type));
                sw.Stop();
                if (outp.Length == 0) throw new Exception(info.Name + " returned nothing");
                foreach (var v in outp)
                    if (float.IsNaN(v) || float.IsInfinity(v)) throw new Exception(info.Name + " produced NaN");
                if (sw.ElapsedMilliseconds > 400) slow.Add($"{info.Name} {sw.ElapsedMilliseconds} ms");
            }
            return $"{FxCatalog.All.Count} effects ran clean"
                 + (slow.Count > 0 ? " (slow: " + string.Join(", ", slow) + ")" : "");
        });

        Check("Mixdown", () =>
        {
            var project = new Models.EditorProject();
            var a = project.AddSource("A", "self-test", MakeVoice(220, 1.0));
            var b = project.AddSource("B", "self-test", MakeVoice(330, 1.0));
            var track = new Models.EditorTrack { Name = "one" };
            var under = new Models.EditorTrack { Name = "two" };
            project.Tracks.Add(track);
            project.Tracks.Add(under);
            track.Items.Add(new Models.TimelineItem { SourceId = a.Id, Start = 0, TrimIn = 0, TrimOut = 1 });
            track.Items.Add(new Models.TimelineItem { SourceId = b.Id, Start = 1.5, TrimIn = 0, TrimOut = 1 });
            under.Items.Add(new Models.TimelineItem { SourceId = a.Id, Start = 0.5, TrimIn = 0, TrimOut = 0.5, Gain = 0.5 });

            var mix = Mixdown.Render(project);
            var seconds = WavIO.Duration(mix);
            if (Math.Abs(seconds - 2.5) > 0.03) throw new Exception($"two spliced blocks came to {seconds:0.00}s, wanted 2.50s");
            if (Dsp.Peak(mix) > 1.001f) throw new Exception($"mix came back over 0 dBFS ({Dsp.Peak(mix):0.00})");
            return $"3 blocks across 2 lanes → {seconds:0.00}s, peak {Dsp.Peak(mix):0.00}";
        });

        Check("GitHub update check", () =>
        {
            // Task.Run keeps this off whatever thread the self-test was started from
            var check = Task.Run(() => UpdateService.CheckAsync()).GetAwaiter().GetResult();
            if (check.Error != null) throw new Exception(check.Error);

            var bits = new List<string> { check.Summary };
            if (check.Release != null)
                bits.Add($"latest release \"{check.Release.Tag}\", build attached: "
                         + (check.Release.AssetName ?? "none"));
            if (!string.IsNullOrEmpty(check.LatestCommitSha))
                bits.Add("main at " + check.LatestCommitSha[..8]);
            return string.Join(" | ", bits);
        });

        try { Directory.Delete(dir, true); } catch { }

        sb.AppendLine("────────────────────────");
        sb.AppendLine($"{pass} passed, {fail} failed.");
        if (fail == 0) sb.AppendLine("Everything in the audio path is working.");

        var report = sb.ToString();
        AppPaths.Log("Self-test:\n" + report);
        return report;
    }

    /// <summary>
    /// Walks the Ogg page chain and returns the page count and the last granule
    /// position (total samples encoded).
    /// </summary>
    private static (int pages, long granule) WalkOggPages(byte[] data)
    {
        int pos = 0, pages = 0;
        long granule = 0;

        while (pos + 27 <= data.Length)
        {
            if (data[pos] != 'O' || data[pos + 1] != 'g' || data[pos + 2] != 'g' || data[pos + 3] != 'S')
                throw new Exception($"page {pages} has no OggS marker at byte {pos}");

            granule = BitConverter.ToInt64(data, pos + 6);
            int segments = data[pos + 26];
            int tableEnd = pos + 27 + segments;
            if (tableEnd > data.Length) throw new Exception("segment table runs past end of file");

            int body = 0;
            for (int i = 0; i < segments; i++) body += data[pos + 27 + i];

            pos = tableEnd + body;
            pages++;
        }

        if (pos != data.Length) throw new Exception("trailing bytes after the last page");
        return (pages, granule);
    }

    /// <summary>A plain sine burst for the playback probe.</summary>
    private static float[] Beep(double seconds, float amplitude)
    {
        var frames = (int)(Fmt.Rate * seconds);
        var data = new float[frames * Fmt.Channels];
        for (int f = 0; f < frames; f++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * 660 * f / Fmt.Rate));
            data[f * 2] = v;
            data[f * 2 + 1] = v;
        }
        Dsp.Fade(data, Fmt.Channels, 15, 40, Fmt.Rate);
        return data;
    }

    /// <summary>Silence, then a two-tone chord, then silence — exercises trimming too.</summary>
    /// <summary>A buzzy sawtooth — enough harmonics for the pitch tracker to bite on.</summary>
    private static float[] MakeVoice(double hz, double seconds)
    {
        var frames = (int)(seconds * Fmt.Rate);
        var data = new float[frames * Fmt.Channels];
        double phase = 0;
        for (int f = 0; f < frames; f++)
        {
            phase += hz / Fmt.Rate;
            if (phase >= 1) phase -= 1;
            var v = (float)((2 * phase - 1) * 0.4);
            for (int c = 0; c < Fmt.Channels; c++) data[f * Fmt.Channels + c] = v;
        }
        return data;
    }

    /// <summary>The median of the confident pitch estimates across a buffer.</summary>
    private static double MedianPitch(float[] stereo)
    {
        var mono = new float[stereo.Length / Fmt.Channels];
        for (int f = 0; f < mono.Length; f++) mono[f] = stereo[f * Fmt.Channels];

        var frames = Spectral.FrameCount(mono.Length);
        PitchTrack.Detect(mono, frames, out var hz, out var confidence);

        var votes = new List<double>();
        for (int f = frames / 6; f < frames * 5 / 6; f++)
            if (confidence[f] > 0.6 && hz[f] > 0) votes.Add(hz[f]);
        if (votes.Count == 0) throw new Exception("no pitch found at all");
        votes.Sort();
        return votes[votes.Count / 2];
    }

    private static float[] MakeTone(double seconds)
    {
        var frames = (int)(Fmt.Rate * seconds);
        var data = new float[frames * Fmt.Channels];
        var quietHead = (int)(Fmt.Rate * 0.4);
        var quietTail = frames - (int)(Fmt.Rate * 0.4);

        for (int f = 0; f < frames; f++)
        {
            float v = 0;
            if (f > quietHead && f < quietTail)
            {
                var t = f / (double)Fmt.Rate;
                v = (float)(0.55 * Math.Sin(2 * Math.PI * 440 * t)
                          + 0.25 * Math.Sin(2 * Math.PI * 880 * t));
            }
            data[f * 2] = v;
            data[f * 2 + 1] = v * 0.85f;
        }
        return data;
    }
}
