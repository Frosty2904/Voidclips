using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Serialization;
using VoidClip.Audio;

namespace VoidClip.Models;

// ══════════════════════════════════════════════════════════════
//  ONE EFFECT IN A CHAIN
//
//  Deliberately dumb: a type, an on/off, and a bag of numbers
//  whose meaning comes from FxCatalog. That is what lets the
//  editor build its whole rack from the catalogue alone.
// ══════════════════════════════════════════════════════════════
public class FxUnit : Bindable
{
    private bool _enabled = true;

    public FxType Type { get; set; }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public Dictionary<string, double> Values { get; set; } = new();

    public FxUnit() { }

    public FxUnit(FxType type)
    {
        Type = type;
        Reset();
    }

    [JsonIgnore] public FxInfo Info => FxCatalog.Info(Type);
    [JsonIgnore] public string Name => Info.Name;

    public void Reset()
    {
        Values.Clear();
        foreach (var p in Info.Params) Values[p.Key] = p.Default;
        Raise(nameof(Values));
    }

    public double Get(string key)
    {
        if (Values.TryGetValue(key, out var v)) return v;
        foreach (var p in Info.Params) if (p.Key == key) return p.Default;
        return 0;
    }

    public bool On(string key) => Get(key) >= 0.5;
    public int Choice(string key) => (int)Math.Round(Get(key));

    public void Put(string key, double value)
    {
        Values[key] = value;
        Raise(nameof(Values));
    }

    /// <summary>True when every parameter still sits at its default.</summary>
    [JsonIgnore]
    public bool IsDefault => Info.Params.All(p => Math.Abs(Get(p.Key) - p.Default) < 1e-9);

    public FxUnit Clone() => new() { Type = Type, Enabled = Enabled, Values = new Dictionary<string, double>(Values) };

    public void Signature(StringBuilder sb)
    {
        sb.Append((int)Type).Append(Enabled ? '+' : '-');
        foreach (var p in Info.Params) sb.Append(p.Key).Append(Get(p.Key).ToString("0.#####")).Append(',');
        sb.Append(';');
    }

    /// <summary>A one-line summary of what this unit is set to, for the rack header.</summary>
    public string Summary()
    {
        var parts = new List<string>();
        foreach (var p in Info.Params)
        {
            var v = Get(p.Key);
            if (Math.Abs(v - p.Default) < 1e-9) continue;
            parts.Add($"{p.Label} {p.Display(v)}");
            if (parts.Count == 3) break;
        }
        return parts.Count == 0 ? "default settings" : string.Join(" · ", parts);
    }
}

// ══════════════════════════════════════════════════════════════
//  A CHAIN OF THEM
// ══════════════════════════════════════════════════════════════
public class FxChain : Bindable
{
    public ObservableCollection<FxUnit> Units { get; set; } = new();

    [JsonIgnore] public int ActiveCount => Units.Count(u => u.Enabled);
    [JsonIgnore] public bool IsEmpty => Units.Count == 0;

    public FxChain Clone()
    {
        var c = new FxChain();
        foreach (var u in Units) c.Units.Add(u.Clone());
        return c;
    }

    public void Signature(StringBuilder sb)
    {
        foreach (var u in Units) u.Signature(sb);
        sb.Append('|');
    }

    public string Describe()
    {
        if (Units.Count == 0) return "no effects";
        var names = Units.Where(u => u.Enabled).Select(u => u.Name).ToList();
        if (names.Count == 0) return $"{Units.Count} effect(s), all bypassed";
        return string.Join(" → ", names);
    }
}

// ══════════════════════════════════════════════════════════════
//  SOURCE AUDIO — a library clip, an imported file, or the buffer
// ══════════════════════════════════════════════════════════════
public class SourceAudio
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Where it came from, shown under the name in the source list.</summary>
    public string Origin { get; set; } = "";
    /// <summary>Set when this source is a clip already in the library.</summary>
    public string ClipId { get; set; } = "";

    [JsonIgnore] public float[] Data { get; set; } = Array.Empty<float>();
    [JsonIgnore] public float[] Peaks { get; set; }

    public double Duration => Data.Length / (double)(Fmt.Rate * Fmt.Channels);

    public string DurationText => Duration >= 60
        ? $"{(int)(Duration / 60)}:{(int)(Duration % 60):00}"
        : $"{Duration:0.0}s";
}

// ══════════════════════════════════════════════════════════════
//  ONE BLOCK ON THE TIMELINE
// ══════════════════════════════════════════════════════════════
public class TimelineItem : Bindable
{
    private string _name = "";
    private double _start;
    private double _trimIn;
    private double _trimOut;
    private double _gain = 1;
    private double _fadeIn;
    private double _fadeOut;
    private bool _muted;
    private bool _selected;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceId { get; set; } = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Where the block sits on the timeline, in seconds.</summary>
    public double Start { get => _start; set => Set(ref _start, Math.Max(0, value)); }

    /// <summary>In and out points inside the source, in seconds.</summary>
    public double TrimIn { get => _trimIn; set { if (Set(ref _trimIn, Math.Max(0, value))) Invalidate(); } }
    public double TrimOut { get => _trimOut; set { if (Set(ref _trimOut, Math.Max(0, value))) Invalidate(); } }

    public double Gain { get => _gain; set => Set(ref _gain, Math.Clamp(value, 0, 3)); }
    public double FadeIn { get => _fadeIn; set => Set(ref _fadeIn, Math.Max(0, value)); }
    public double FadeOut { get => _fadeOut; set => Set(ref _fadeOut, Math.Max(0, value)); }
    public bool Muted { get => _muted; set => Set(ref _muted, value); }

    public FxChain Chain { get; set; } = new();

    [JsonIgnore] public bool Selected { get => _selected; set => Set(ref _selected, value); }

    /// <summary>Length of the trimmed source before any effects run.</summary>
    [JsonIgnore] public double RawLength => Math.Max(0, TrimOut - TrimIn);

    // ── render cache ──
    [JsonIgnore] public float[] Rendered { get; set; }
    [JsonIgnore] public float[] Peaks { get; set; }
    [JsonIgnore] public string RenderSignature { get; set; }

    /// <summary>
    /// How long the block actually is once its effects have run — pitch, speed,
    /// reverse and reverb tails all move this, so the timeline asks after rendering.
    /// </summary>
    [JsonIgnore]
    public double Length => Rendered != null
        ? Rendered.Length / (double)(Fmt.Rate * Fmt.Channels)
        : RawLength;

    [JsonIgnore] public double End => Start + Length;

    public void Invalidate()
    {
        Rendered = null;
        Peaks = null;
        RenderSignature = null;
    }

    public string BuildSignature()
    {
        var sb = new StringBuilder();
        sb.Append(SourceId).Append('@').Append(TrimIn.ToString("0.#####")).Append(':')
          .Append(TrimOut.ToString("0.#####")).Append('|');
        Chain.Signature(sb);
        return sb.ToString();
    }

    public TimelineItem Clone() => new()
    {
        Id = Id,
        SourceId = SourceId,
        Name = Name,
        Start = Start,
        TrimIn = TrimIn,
        TrimOut = TrimOut,
        Gain = Gain,
        FadeIn = FadeIn,
        FadeOut = FadeOut,
        Muted = Muted,
        Chain = Chain.Clone(),
        Rendered = Rendered,
        Peaks = Peaks,
        RenderSignature = RenderSignature,
    };
}

// ══════════════════════════════════════════════════════════════
//  A LANE
// ══════════════════════════════════════════════════════════════
public class EditorTrack : Bindable
{
    private string _name = "Track";
    private bool _muted;
    private bool _solo;
    private double _gain = 1;
    private double _pan;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Muted { get => _muted; set => Set(ref _muted, value); }
    public bool Solo { get => _solo; set => Set(ref _solo, value); }
    public double Gain { get => _gain; set => Set(ref _gain, Math.Clamp(value, 0, 3)); }
    public double Pan { get => _pan; set => Set(ref _pan, Math.Clamp(value, -1, 1)); }

    public FxChain Chain { get; set; } = new();
    public ObservableCollection<TimelineItem> Items { get; set; } = new();

    public double End => Items.Count == 0 ? 0 : Items.Max(i => i.End);

    public EditorTrack Clone()
    {
        var t = new EditorTrack
        {
            Id = Id, Name = Name, Muted = Muted, Solo = Solo,
            Gain = Gain, Pan = Pan, Chain = Chain.Clone()
        };
        foreach (var i in Items) t.Items.Add(i.Clone());
        return t;
    }
}

// ══════════════════════════════════════════════════════════════
//  THE WHOLE ARRANGEMENT
// ══════════════════════════════════════════════════════════════
public class EditorProject : Bindable
{
    public ObservableCollection<EditorTrack> Tracks { get; set; } = new();
    public FxChain Master { get; set; } = new();

    /// <summary>Every piece of audio the arrangement can point at, by id.</summary>
    [JsonIgnore] public Dictionary<string, SourceAudio> Sources { get; } = new();

    public SourceAudio Source(string id)
        => id != null && Sources.TryGetValue(id, out var s) ? s : null;

    public SourceAudio AddSource(string name, string origin, float[] data, string clipId = "")
    {
        var s = new SourceAudio { Name = name, Origin = origin, Data = data, ClipId = clipId };
        s.Peaks = Dsp.Peaks(data, Fmt.Channels, 96);
        Sources[s.Id] = s;
        return s;
    }

    public IEnumerable<TimelineItem> AllItems => Tracks.SelectMany(t => t.Items);

    public EditorTrack TrackOf(TimelineItem item) => Tracks.FirstOrDefault(t => t.Items.Contains(item));

    public double Duration => Tracks.Count == 0 ? 0 : Math.Max(0, Tracks.Max(t => t.End));

    public bool AnySolo => Tracks.Any(t => t.Solo);

    public bool Audible(EditorTrack t) => !t.Muted && (!AnySolo || t.Solo);

    /// <summary>Structure only — the audio itself is shared, never copied.</summary>
    public EditorProject Clone()
    {
        var p = new EditorProject { Master = Master.Clone() };
        foreach (var t in Tracks) p.Tracks.Add(t.Clone());
        foreach (var kv in Sources) p.Sources[kv.Key] = kv.Value;
        return p;
    }

    /// <summary>Restores this project's contents from a snapshot taken by <see cref="Clone"/>.</summary>
    public void RestoreFrom(EditorProject snapshot)
    {
        Tracks.Clear();
        foreach (var t in snapshot.Tracks) Tracks.Add(t.Clone());
        Master = snapshot.Master.Clone();
        foreach (var kv in snapshot.Sources) Sources[kv.Key] = kv.Value;
        Raise(nameof(Tracks));
        Raise(nameof(Master));
    }
}
