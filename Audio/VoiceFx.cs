using VoidClip.Models;

namespace VoidClip.Audio;

// ══════════════════════════════════════════════════════════════
//  THE EFFECT CATALOGUE
//
//  Every effect is described here — its name, its parameters and
//  their ranges — and the editor builds its whole UI from this.
//  Adding an effect means adding an entry here and a case in
//  FxProcessor.Run; nothing in the views has to change.
// ══════════════════════════════════════════════════════════════
public enum FxType
{
    Pitch, AutoTune, Formant, Harmonizer, Speed,
    Eq, Filter, Distortion, BitCrush,
    Chorus, Flanger, Phaser, Vibrato, Tremolo, RingMod,
    Delay, Reverb,
    Compressor, Gate,
    Robot, Whisper, Width, Reverse, Gain
}

public enum FxParamKind { Slider, Choice, Toggle }

public sealed class FxParam
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; } = 1;
    public double Default { get; init; }
    public string Format { get; init; } = "0.##";
    /// <summary>Slider travel is logarithmic — right for frequencies and times.</summary>
    public bool Log { get; init; }
    public string[] Choices { get; init; }
    public FxParamKind Kind { get; init; } = FxParamKind.Slider;

    public double Clamp(double v) => Math.Clamp(v, Min, Max);

    /// <summary>Value to 0..1 slider position.</summary>
    public double ToSlider(double v)
    {
        v = Clamp(v);
        if (!Log) return Max - Min < 1e-12 ? 0 : (v - Min) / (Max - Min);
        var lo = Math.Log(Math.Max(1e-6, Min));
        var hi = Math.Log(Math.Max(1e-6, Max));
        return (Math.Log(Math.Max(1e-6, v)) - lo) / (hi - lo);
    }

    /// <summary>0..1 slider position back to a value.</summary>
    public double FromSlider(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (!Log) return Min + t * (Max - Min);
        var lo = Math.Log(Math.Max(1e-6, Min));
        var hi = Math.Log(Math.Max(1e-6, Max));
        return Math.Exp(lo + t * (hi - lo));
    }

    public string Display(double v) => Kind switch
    {
        FxParamKind.Toggle => v >= 0.5 ? "on" : "off",
        FxParamKind.Choice => Choices != null && Choices.Length > 0
            ? Choices[(int)Math.Clamp(Math.Round(v), 0, Choices.Length - 1)]
            : v.ToString("0"),
        _ => v.ToString(Format) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit)
    };
}

public sealed class FxInfo
{
    public FxType Type { get; init; }
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public string Blurb { get; init; } = "";
    public FxParam[] Params { get; init; } = Array.Empty<FxParam>();
}

// ══════════════════════════════════════════════════════════════
//  NOTES AND SCALES — what auto-tune snaps to
// ══════════════════════════════════════════════════════════════
public static class Music
{
    public static readonly string[] NoteNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static readonly string[] ScaleNames =
    {
        "Chromatic", "Major", "Natural minor", "Harmonic minor", "Major pentatonic",
        "Minor pentatonic", "Blues", "Dorian", "Phrygian", "Lydian", "Mixolydian",
        "Locrian", "Whole tone", "Root + fifth"
    };

    /// <summary>Semitone offsets from the root, one row per scale above.</summary>
    public static readonly int[][] Scales =
    {
        new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },
        new[] { 0, 2, 4, 5, 7, 9, 11 },
        new[] { 0, 2, 3, 5, 7, 8, 10 },
        new[] { 0, 2, 3, 5, 7, 8, 11 },
        new[] { 0, 2, 4, 7, 9 },
        new[] { 0, 3, 5, 7, 10 },
        new[] { 0, 3, 5, 6, 7, 10 },
        new[] { 0, 2, 3, 5, 7, 9, 10 },
        new[] { 0, 1, 3, 5, 7, 8, 10 },
        new[] { 0, 2, 4, 6, 7, 9, 11 },
        new[] { 0, 2, 4, 5, 7, 9, 10 },
        new[] { 0, 1, 3, 5, 6, 8, 10 },
        new[] { 0, 2, 4, 6, 8, 10 },
        new[] { 0, 7 },
    };

    public static bool[] Mask(int key, int scale)
    {
        var mask = new bool[12];
        var set = Scales[Math.Clamp(scale, 0, Scales.Length - 1)];
        foreach (var s in set) mask[((s + key) % 12 + 12) % 12] = true;
        return mask;
    }

    public static double HzToMidi(double hz) => 69 + 12 * Math.Log2(Math.Max(1e-6, hz) / 440.0);
    public static double MidiToHz(double midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    public static string NoteName(double midi)
    {
        var n = (int)Math.Round(midi);
        return NoteNames[((n % 12) + 12) % 12] + (n / 12 - 1);
    }

    /// <summary>Nearest note in the mask. Fractional MIDI in, whole MIDI out.</summary>
    public static double Snap(double midi, bool[] mask)
    {
        var best = double.NaN;
        var bestDist = double.MaxValue;
        var centre = (int)Math.Round(midi);
        for (int n = centre - 7; n <= centre + 7; n++)
        {
            if (!mask[((n % 12) + 12) % 12]) continue;
            var d = Math.Abs(n - midi);
            if (d < bestDist) { bestDist = d; best = n; }
        }
        return double.IsNaN(best) ? midi : best;
    }
}

public static class FxCatalog
{
    private static FxParam S(string key, string label, double min, double max, double def,
                             string unit = "", string fmt = "0.##", bool log = false)
        => new() { Key = key, Label = label, Min = min, Max = max, Default = def, Unit = unit, Format = fmt, Log = log };

    private static FxParam T(string key, string label, bool def)
        => new() { Key = key, Label = label, Min = 0, Max = 1, Default = def ? 1 : 0, Kind = FxParamKind.Toggle };

    private static FxParam C(string key, string label, double def, params string[] choices)
        => new() { Key = key, Label = label, Min = 0, Max = choices.Length - 1, Default = def, Kind = FxParamKind.Choice, Choices = choices };

    private static readonly FxInfo[] _all =
    {
        new()
        {
            Type = FxType.Pitch, Name = "Pitch shift", Group = "Voice",
            Blurb = "Moves the voice up or down without changing how long the clip runs.",
            Params = new[]
            {
                S("semitones", "Pitch", -24, 24, 0, "st"),
                S("cents", "Fine tune", -100, 100, 0, "cents", "0"),
                S("formant", "Formant", -12, 12, 0, "st"),
                T("preserve", "Keep formants", true),
                S("mix", "Mix", 0, 100, 100, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.AutoTune, Name = "Auto-tune", Group = "Voice",
            Blurb = "Tracks the pitch of the voice and pulls every note onto the chosen scale. "
                  + "Retune at 0 ms is the hard, obviously-tuned sound.",
            Params = new[]
            {
                C("key", "Key", 0, Music.NoteNames),
                C("scale", "Scale", 1, Music.ScaleNames),
                S("strength", "Strength", 0, 100, 90, "%", "0"),
                S("retune", "Retune speed", 0, 400, 15, "ms", "0"),
                S("transpose", "Transpose", -12, 12, 0, "st", "0"),
                S("vibrato", "Vibrato", 0, 100, 0, "cents", "0"),
                S("vibratoRate", "Vibrato rate", 0.5, 12, 5, "Hz", "0.#"),
                S("sensitivity", "Note detection", 0, 100, 40, "%", "0"),
                T("preserve", "Keep formants", true),
                S("mix", "Mix", 0, 100, 100, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Formant, Name = "Formant", Group = "Voice",
            Blurb = "Changes the size of the speaker — throat and mouth — without touching the note they sang.",
            Params = new[]
            {
                S("shift", "Formant", -12, 12, 0, "st"),
                S("mix", "Mix", 0, 100, 100, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Harmonizer, Name = "Harmoniser", Group = "Voice",
            Blurb = "Stacks pitch-shifted copies underneath the voice. A voice at 0 level is off.",
            Params = new[]
            {
                S("v1", "Voice 1", -24, 24, 4, "st", "0"),
                S("v1level", "Voice 1 level", 0, 100, 55, "%", "0"),
                S("v2", "Voice 2", -24, 24, 7, "st", "0"),
                S("v2level", "Voice 2 level", 0, 100, 45, "%", "0"),
                S("v3", "Voice 3", -24, 24, -12, "st", "0"),
                S("v3level", "Voice 3 level", 0, 100, 0, "%", "0"),
                S("detune", "Detune", 0, 50, 8, "cents", "0"),
                S("spread", "Stereo spread", 0, 100, 60, "%", "0"),
                S("dry", "Dry level", 0, 100, 100, "%", "0"),
                T("preserve", "Keep formants", true),
            }
        },
        new()
        {
            Type = FxType.Speed, Name = "Speed", Group = "Voice",
            Blurb = "Plays the segment faster or slower. With Keep pitch off it behaves like a tape machine.",
            Params = new[]
            {
                S("rate", "Rate", 0.25, 4, 1, "x", "0.###", true),
                T("keepPitch", "Keep pitch", true),
            }
        },
        new()
        {
            Type = FxType.Eq, Name = "EQ", Group = "Tone",
            Blurb = "Three bands: a low shelf, a sweepable mid and a high shelf.",
            Params = new[]
            {
                S("lowGain", "Low gain", -18, 18, 0, "dB", "0.#"),
                S("lowFreq", "Low freq", 40, 600, 160, "Hz", "0", true),
                S("midGain", "Mid gain", -18, 18, 0, "dB", "0.#"),
                S("midFreq", "Mid freq", 200, 8000, 1400, "Hz", "0", true),
                S("midQ", "Mid width", 0.3, 6, 1, "Q"),
                S("highGain", "High gain", -18, 18, 0, "dB", "0.#"),
                S("highFreq", "High freq", 1500, 16000, 6000, "Hz", "0", true),
            }
        },
        new()
        {
            Type = FxType.Filter, Name = "Filter", Group = "Tone",
            Blurb = "One resonant filter. A band pass around 1.5 kHz is most of a telephone.",
            Params = new[]
            {
                C("type", "Type", 0, "Low pass", "High pass", "Band pass", "Notch"),
                S("cutoff", "Cutoff", 40, 18000, 2000, "Hz", "0", true),
                S("res", "Resonance", 0.3, 12, 0.8, "Q"),
                S("mix", "Mix", 0, 100, 100, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Distortion, Name = "Distortion", Group = "Character",
            Blurb = "Drives the signal into clipping. Fuzz and Fold get ugly fast, which is usually the point.",
            Params = new[]
            {
                C("type", "Type", 0, "Soft", "Hard", "Fuzz", "Fold"),
                S("drive", "Drive", 0, 48, 12, "dB", "0.#"),
                S("tone", "Tone", 400, 16000, 9000, "Hz", "0", true),
                S("mix", "Mix", 0, 100, 100, "%", "0"),
                S("out", "Output", -24, 12, 0, "dB", "0.#"),
            }
        },
        new()
        {
            Type = FxType.BitCrush, Name = "Bit crush", Group = "Character",
            Blurb = "Throws away bit depth and sample rate. Low numbers sound like a 90s answerphone.",
            Params = new[]
            {
                S("bits", "Bit depth", 1, 16, 8, "bit", "0"),
                S("down", "Rate divide", 1, 64, 4, "x", "0"),
                S("mix", "Mix", 0, 100, 100, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Chorus, Name = "Chorus", Group = "Character",
            Blurb = "Detuned copies drifting against the original. Thickens a thin voice.",
            Params = new[]
            {
                S("rate", "Rate", 0.05, 8, 0.7, "Hz", "0.##", true),
                S("depth", "Depth", 0.5, 20, 4, "ms", "0.#"),
                S("voices", "Voices", 1, 4, 2, "", "0"),
                S("spread", "Stereo spread", 0, 100, 70, "%", "0"),
                S("mix", "Mix", 0, 100, 40, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Flanger, Name = "Flanger", Group = "Character",
            Blurb = "A short sweeping delay fed back on itself — the jet-plane whoosh.",
            Params = new[]
            {
                S("rate", "Rate", 0.02, 5, 0.25, "Hz", "0.###", true),
                S("depth", "Depth", 0.1, 10, 3, "ms"),
                S("feedback", "Feedback", -95, 95, 60, "%", "0"),
                S("mix", "Mix", 0, 100, 50, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Phaser, Name = "Phaser", Group = "Character",
            Blurb = "Sweeping notches. Subtler than a flanger and good under a whisper.",
            Params = new[]
            {
                S("rate", "Rate", 0.02, 6, 0.4, "Hz", "0.###", true),
                S("depth", "Depth", 0, 100, 70, "%", "0"),
                C("stages", "Stages", 1, "2", "4", "6", "8"),
                S("feedback", "Feedback", -95, 95, 45, "%", "0"),
                S("mix", "Mix", 0, 100, 50, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Vibrato, Name = "Vibrato", Group = "Character",
            Blurb = "Wobbles the pitch. A little is singing, a lot is seasick.",
            Params = new[]
            {
                S("rate", "Rate", 0.1, 12, 5, "Hz"),
                S("depth", "Depth", 0, 100, 30, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Tremolo, Name = "Tremolo", Group = "Character",
            Blurb = "Wobbles the volume instead of the pitch.",
            Params = new[]
            {
                S("rate", "Rate", 0.1, 20, 5, "Hz"),
                S("depth", "Depth", 0, 100, 60, "%", "0"),
                C("shape", "Shape", 0, "Sine", "Triangle", "Square"),
                S("stereo", "Stereo", 0, 100, 0, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.RingMod, Name = "Ring mod", Group = "Character",
            Blurb = "Multiplies the voice by a tone. Metallic, inhuman, very Dalek.",
            Params = new[]
            {
                S("freq", "Frequency", 10, 3000, 110, "Hz", "0", true),
                S("mix", "Mix", 0, 100, 60, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Delay, Name = "Delay", Group = "Space",
            Blurb = "Echoes. Damping rolls the top off each repeat so they fade into the background.",
            Params = new[]
            {
                S("time", "Time", 10, 2000, 280, "ms", "0", true),
                S("feedback", "Feedback", 0, 95, 35, "%", "0"),
                S("damping", "Damping", 500, 16000, 5000, "Hz", "0", true),
                T("pingpong", "Ping-pong", false),
                S("mix", "Mix", 0, 100, 28, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Reverb, Name = "Reverb", Group = "Space",
            Blurb = "Puts the voice in a room. Size 100 is a cathedral you will regret.",
            Params = new[]
            {
                S("size", "Room size", 0, 100, 60, "%", "0"),
                S("damping", "Damping", 0, 100, 45, "%", "0"),
                S("width", "Width", 0, 100, 90, "%", "0"),
                S("predelay", "Pre-delay", 0, 250, 15, "ms", "0"),
                S("mix", "Mix", 0, 100, 25, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Compressor, Name = "Compressor", Group = "Dynamics",
            Blurb = "Evens out the loud and quiet parts so a clip cuts through a call.",
            Params = new[]
            {
                S("threshold", "Threshold", -60, 0, -18, "dB", "0.#"),
                S("ratio", "Ratio", 1, 20, 4, ":1", "0.#"),
                S("attack", "Attack", 0.5, 200, 10, "ms", "0.#", true),
                S("release", "Release", 10, 1000, 150, "ms", "0", true),
                S("knee", "Knee", 0, 24, 6, "dB", "0.#"),
                S("makeup", "Make-up", -12, 24, 0, "dB", "0.#"),
            }
        },
        new()
        {
            Type = FxType.Gate, Name = "Noise gate", Group = "Dynamics",
            Blurb = "Shuts the signal off between words. Kills hiss and room noise.",
            Params = new[]
            {
                S("threshold", "Threshold", -80, 0, -45, "dB", "0.#"),
                S("attack", "Attack", 0.1, 50, 2, "ms", "0.#", true),
                S("hold", "Hold", 0, 500, 60, "ms", "0"),
                S("release", "Release", 5, 1000, 120, "ms", "0", true),
                S("range", "Depth", -90, 0, -90, "dB", "0"),
            }
        },
        new()
        {
            Type = FxType.Robot, Name = "Robotise", Group = "Character",
            Blurb = "Flattens every frame to the same phase, so the voice loses its note and becomes a machine.",
            Params = new[]
            {
                S("formant", "Formant", -12, 12, 0, "st"),
                S("mix", "Mix", 0, 100, 100, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Whisper, Name = "Whisperise", Group = "Character",
            Blurb = "Randomises phase, which removes the note and leaves the breath.",
            Params = new[]
            {
                S("formant", "Formant", -12, 12, 0, "st"),
                S("mix", "Mix", 0, 100, 100, "%", "0"),
            }
        },
        new()
        {
            Type = FxType.Width, Name = "Stereo", Group = "Tone",
            Blurb = "Widens or narrows the stereo image and moves it left or right.",
            Params = new[]
            {
                S("width", "Width", 0, 200, 100, "%", "0"),
                S("pan", "Pan", -100, 100, 0, "", "0"),
            }
        },
        new()
        {
            Type = FxType.Reverse, Name = "Reverse", Group = "Character",
            Blurb = "Plays the segment backwards.",
            Params = Array.Empty<FxParam>()
        },
        new()
        {
            Type = FxType.Gain, Name = "Gain", Group = "Dynamics",
            Blurb = "Level trim, with an optional normalise and a ceiling to stop it clipping.",
            Params = new[]
            {
                S("gain", "Gain", -30, 24, 0, "dB", "0.#"),
                T("normalise", "Normalise first", false),
                S("ceiling", "Ceiling", -12, 0, -1, "dB", "0.#"),
            }
        },
    };

    public static IReadOnlyList<FxInfo> All => _all;

    private static readonly Dictionary<FxType, FxInfo> _byType = _all.ToDictionary(i => i.Type);

    public static FxInfo Info(FxType t) => _byType.TryGetValue(t, out var i) ? i : _all[0];
}

// ══════════════════════════════════════════════════════════════
//  PRESET CHAINS
// ══════════════════════════════════════════════════════════════
public sealed class FxPreset
{
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public string Blurb { get; init; } = "";
    public Func<List<FxUnit>> Build { get; init; }
}

public static class FxPresets
{
    private static FxUnit U(FxType t, params (string key, double value)[] values)
    {
        var u = new FxUnit(t);
        foreach (var (k, v) in values) u.Put(k, v);
        return u;
    }

    private static List<FxUnit> Chain(params FxUnit[] units) => units.ToList();

    public static readonly FxPreset[] All =
    {
        new() { Name = "Clean up", Group = "Repair", Blurb = "Gate, compress, lift — what a rough clip usually needs",
                Build = () => Chain(
                    U(FxType.Gate, ("threshold", -48), ("release", 140)),
                    U(FxType.Eq, ("lowGain", -3), ("lowFreq", 110), ("midGain", 2), ("midFreq", 2400), ("highGain", 2)),
                    U(FxType.Compressor, ("threshold", -20), ("ratio", 3.5), ("makeup", 3)),
                    U(FxType.Gain, ("normalise", 1), ("ceiling", -1))) },

        new() { Name = "Chipmunk", Group = "Voice", Blurb = "Up an octave, formants and all",
                Build = () => Chain(U(FxType.Pitch, ("semitones", 12), ("preserve", 0))) },

        new() { Name = "Helium", Group = "Voice", Blurb = "Small throat, same note",
                Build = () => Chain(U(FxType.Pitch, ("semitones", 5), ("formant", 7), ("preserve", 1))) },

        new() { Name = "Demon", Group = "Voice", Blurb = "Down an octave with a growl under it",
                Build = () => Chain(
                    U(FxType.Pitch, ("semitones", -8), ("formant", -4), ("preserve", 1)),
                    U(FxType.Distortion, ("type", 0), ("drive", 14), ("tone", 4000), ("mix", 55)),
                    U(FxType.Reverb, ("size", 70), ("mix", 22))) },

        new() { Name = "Giant", Group = "Voice", Blurb = "Slow, deep and enormous",
                Build = () => Chain(
                    U(FxType.Speed, ("rate", 0.86), ("keepPitch", 0)),
                    U(FxType.Pitch, ("semitones", -3), ("formant", -5), ("preserve", 1)),
                    U(FxType.Reverb, ("size", 82), ("predelay", 40), ("mix", 30))) },

        new() { Name = "Gremlin", Group = "Voice", Blurb = "Fast, high and unhinged",
                Build = () => Chain(
                    U(FxType.Speed, ("rate", 1.18), ("keepPitch", 1)),
                    U(FxType.Pitch, ("semitones", 7), ("formant", 4), ("preserve", 1)),
                    U(FxType.Distortion, ("type", 2), ("drive", 9), ("mix", 35))) },

        new() { Name = "Robot", Group = "Voice", Blurb = "Monotone machine",
                Build = () => Chain(
                    U(FxType.Robot),
                    U(FxType.Eq, ("midGain", 4), ("midFreq", 1800), ("lowGain", -6)),
                    U(FxType.Distortion, ("type", 1), ("drive", 8), ("mix", 30))) },

        new() { Name = "Dalek", Group = "Voice", Blurb = "Ring modulated and furious",
                Build = () => Chain(
                    U(FxType.RingMod, ("freq", 30), ("mix", 85)),
                    U(FxType.Distortion, ("type", 1), ("drive", 12), ("mix", 60)),
                    U(FxType.Filter, ("type", 2), ("cutoff", 1600), ("res", 1.4), ("mix", 70))) },

        new() { Name = "Alien", Group = "Voice", Blurb = "Formant-warped and metallic",
                Build = () => Chain(
                    U(FxType.Pitch, ("semitones", 3), ("formant", 9), ("preserve", 1)),
                    U(FxType.RingMod, ("freq", 220), ("mix", 30)),
                    U(FxType.Flanger, ("rate", 0.3), ("depth", 4), ("feedback", 70), ("mix", 45))) },

        new() { Name = "Ghost", Group = "Voice", Blurb = "Breath with no throat behind it",
                Build = () => Chain(
                    U(FxType.Whisper, ("formant", 2)),
                    U(FxType.Delay, ("time", 320), ("feedback", 45), ("mix", 30)),
                    U(FxType.Reverb, ("size", 88), ("mix", 45))) },

        new() { Name = "Telephone", Group = "Broadcast", Blurb = "Band-limited and thin",
                Build = () => Chain(
                    U(FxType.Filter, ("type", 2), ("cutoff", 1500), ("res", 1.1), ("mix", 100)),
                    U(FxType.Distortion, ("type", 0), ("drive", 10), ("tone", 3500), ("mix", 45)),
                    U(FxType.Compressor, ("threshold", -22), ("ratio", 6), ("makeup", 6))) },

        new() { Name = "Walkie-talkie", Group = "Broadcast", Blurb = "Squashed radio with a bit of crunch",
                Build = () => Chain(
                    U(FxType.Filter, ("type", 2), ("cutoff", 1800), ("res", 2.2)),
                    U(FxType.BitCrush, ("bits", 10), ("down", 3), ("mix", 45)),
                    U(FxType.Distortion, ("type", 1), ("drive", 16), ("tone", 4000), ("mix", 60)),
                    U(FxType.Gate, ("threshold", -42), ("release", 60))) },

        new() { Name = "Megaphone", Group = "Broadcast", Blurb = "Loud, mid-forward and clipped",
                Build = () => Chain(
                    U(FxType.Eq, ("lowGain", -14), ("lowFreq", 300), ("midGain", 10), ("midFreq", 1900), ("midQ", 1.6), ("highGain", -6)),
                    U(FxType.Distortion, ("type", 1), ("drive", 18), ("tone", 5200), ("mix", 80)),
                    U(FxType.Compressor, ("threshold", -18), ("ratio", 8), ("makeup", 5))) },

        new() { Name = "Announcer", Group = "Broadcast", Blurb = "Big radio voice",
                Build = () => Chain(
                    U(FxType.Pitch, ("semitones", -2), ("formant", -2), ("preserve", 1)),
                    U(FxType.Eq, ("lowGain", 5), ("lowFreq", 130), ("midGain", -2), ("midFreq", 700), ("highGain", 4)),
                    U(FxType.Compressor, ("threshold", -24), ("ratio", 5), ("attack", 6), ("makeup", 6)),
                    U(FxType.Reverb, ("size", 40), ("mix", 12))) },

        new() { Name = "Hard tune", Group = "Tuned", Blurb = "The obvious one — zero retune time",
                Build = () => Chain(
                    U(FxType.AutoTune, ("scale", 1), ("strength", 100), ("retune", 0), ("sensitivity", 30)),
                    U(FxType.Compressor, ("threshold", -20), ("ratio", 4), ("makeup", 3))) },

        new() { Name = "Gentle tune", Group = "Tuned", Blurb = "Pulls it into key without announcing itself",
                Build = () => Chain(U(FxType.AutoTune, ("scale", 1), ("strength", 65), ("retune", 90))) },

        new() { Name = "Choir", Group = "Tuned", Blurb = "Tuned, then stacked in thirds and fifths",
                Build = () => Chain(
                    U(FxType.AutoTune, ("scale", 1), ("strength", 100), ("retune", 25)),
                    U(FxType.Harmonizer, ("v1", 4), ("v1level", 60), ("v2", 7), ("v2level", 50), ("v3", 12), ("v3level", 35), ("detune", 12)),
                    U(FxType.Reverb, ("size", 68), ("mix", 30))) },

        new() { Name = "Nightcore", Group = "Speed", Blurb = "Faster and higher",
                Build = () => Chain(U(FxType.Speed, ("rate", 1.28), ("keepPitch", 0))) },

        new() { Name = "Slowed + reverb", Group = "Speed", Blurb = "The 2 a.m. edit",
                Build = () => Chain(
                    U(FxType.Speed, ("rate", 0.82), ("keepPitch", 0)),
                    U(FxType.Reverb, ("size", 78), ("damping", 55), ("mix", 38))) },

        new() { Name = "Underwater", Group = "Space", Blurb = "Muffled and swimming",
                Build = () => Chain(
                    U(FxType.Filter, ("type", 0), ("cutoff", 700), ("res", 1.8)),
                    U(FxType.Chorus, ("rate", 0.3), ("depth", 9), ("mix", 60)),
                    U(FxType.Reverb, ("size", 72), ("damping", 80), ("mix", 40))) },

        new() { Name = "Cave", Group = "Space", Blurb = "Long tail, dark walls",
                Build = () => Chain(
                    U(FxType.Delay, ("time", 420), ("feedback", 55), ("damping", 2500), ("mix", 30)),
                    U(FxType.Reverb, ("size", 92), ("damping", 60), ("predelay", 60), ("mix", 45))) },

        new() { Name = "Stadium", Group = "Space", Blurb = "Ping-pong slapback in a big room",
                Build = () => Chain(
                    U(FxType.Delay, ("time", 260), ("feedback", 40), ("pingpong", 1), ("mix", 35)),
                    U(FxType.Reverb, ("size", 85), ("width", 100), ("mix", 32))) },

        new() { Name = "Cursed", Group = "Character", Blurb = "Everything, all at once",
                Build = () => Chain(
                    U(FxType.Pitch, ("semitones", -5), ("formant", 6), ("preserve", 1)),
                    U(FxType.BitCrush, ("bits", 5), ("down", 7), ("mix", 70)),
                    U(FxType.RingMod, ("freq", 63), ("mix", 40)),
                    U(FxType.Distortion, ("type", 2), ("drive", 22), ("mix", 70)),
                    U(FxType.Reverb, ("size", 60), ("mix", 25))) },

        new() { Name = "Old film", Group = "Character", Blurb = "Wobbly, narrow and worn out",
                Build = () => Chain(
                    U(FxType.Filter, ("type", 2), ("cutoff", 1900), ("res", 0.9), ("mix", 85)),
                    U(FxType.Vibrato, ("rate", 3.2), ("depth", 12)),
                    U(FxType.BitCrush, ("bits", 9), ("down", 2), ("mix", 30)),
                    U(FxType.Width, ("width", 40))) },
    };
}
