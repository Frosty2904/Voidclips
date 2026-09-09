using VoidClip.Models;

namespace VoidClip.Audio;

// ══════════════════════════════════════════════════════════════
//  MIXDOWN
//
//  Turns an arrangement back into one buffer: every block is
//  trimmed, run through its own effects and cached, then laid
//  down at its position; tracks and the master get their own
//  chains on top.
// ══════════════════════════════════════════════════════════════
public static class Mixdown
{
    private const int Ch = Fmt.Channels;
    private static int SamplesPerSecond => Fmt.Rate * Ch;

    public static int ToSamples(double seconds) => (int)Math.Round(seconds * Fmt.Rate) * Ch;
    public static double ToSeconds(int samples) => samples / (double)SamplesPerSecond;

    // ──────────────────────────────────────────────────────────
    //  ONE BLOCK
    // ──────────────────────────────────────────────────────────
    /// <summary>Renders a block if its cache is stale, and returns the audio.</summary>
    public static float[] RenderItem(EditorProject project, TimelineItem item)
    {
        var signature = item.BuildSignature();
        if (item.Rendered != null && item.RenderSignature == signature) return item.Rendered;

        var source = project.Source(item.SourceId);
        if (source == null || source.Data.Length == 0)
        {
            item.Rendered = Array.Empty<float>();
            item.Peaks = Array.Empty<float>();
            item.RenderSignature = signature;
            return item.Rendered;
        }

        var total = source.Data.Length;
        var from = Math.Clamp(ToSamples(item.TrimIn), 0, total);
        var to = item.TrimOut <= item.TrimIn ? total : Math.Clamp(ToSamples(item.TrimOut), from, total);

        var slice = new float[to - from];
        Array.Copy(source.Data, from, slice, 0, slice.Length);

        var processed = FxProcessor.Run(slice, item.Chain.Units);

        item.Rendered = processed;
        item.Peaks = Dsp.Peaks(processed, Ch, PeakBuckets(processed.Length));
        item.RenderSignature = signature;
        return processed;
    }

    private static int PeakBuckets(int samples)
        => (int)Math.Clamp(ToSeconds(samples) * 140, 32, 8000);

    /// <summary>Brings every block's cache up to date. Safe to call on a background thread.</summary>
    public static void RenderAll(EditorProject project, Action<string> progress = null)
    {
        var items = project.AllItems.ToList();
        for (int i = 0; i < items.Count; i++)
        {
            progress?.Invoke($"Rendering block {i + 1} of {items.Count}…");
            RenderItem(project, items[i]);
        }
    }

    // ──────────────────────────────────────────────────────────
    //  THE WHOLE ARRANGEMENT
    // ──────────────────────────────────────────────────────────
    public static float[] Render(EditorProject project, Action<string> progress = null, bool applyMaster = true)
    {
        RenderAll(project, progress);

        var buffers = new List<float[]>();
        var audible = project.Tracks.Where(project.Audible).ToList();

        foreach (var track in audible)
        {
            progress?.Invoke($"Mixing {track.Name}…");
            var buffer = RenderTrack(project, track);
            if (buffer.Length > 0) buffers.Add(buffer);
        }

        if (buffers.Count == 0) return Array.Empty<float>();

        var length = buffers.Max(b => b.Length);
        var mix = new float[length];
        foreach (var b in buffers)
            for (int i = 0; i < b.Length; i++) mix[i] += b[i];

        Contain(mix);

        if (applyMaster && project.Master.Units.Count > 0)
        {
            progress?.Invoke("Applying master effects…");
            mix = FxProcessor.Run(mix, project.Master.Units);
            Contain(mix);
        }
        return mix;
    }

    /// <summary>
    /// Turns a hot mix down until it fits, rather than clipping it. Blocks and
    /// effects are free to run over 0 dBFS on the way here; this is where that
    /// gets paid for, once, at the end.
    /// </summary>
    private static void Contain(float[] mix)
    {
        var peak = Dsp.Peak(mix);
        if (peak <= 0.999f || peak <= 0) return;
        Dsp.ApplyGain(mix, 0.999f / peak);
    }

    private static float[] RenderTrack(EditorProject project, EditorTrack track)
    {
        var items = track.Items.Where(i => !i.Muted).ToList();
        if (items.Count == 0) return Array.Empty<float>();

        var length = 0;
        foreach (var item in items)
        {
            var audio = RenderItem(project, item);
            length = Math.Max(length, ToSamples(item.Start) + audio.Length);
        }
        if (length <= 0) return Array.Empty<float>();

        var buffer = new float[length];
        foreach (var item in items) Place(buffer, project, item);

        if (track.Chain.Units.Count > 0) buffer = FxProcessor.Run(buffer, track.Chain.Units);

        var pan = track.Pan;
        var angle = (pan + 1) * Math.PI / 4;
        var lGain = track.Gain * Math.Cos(angle) * Math.Sqrt(2);
        var rGain = track.Gain * Math.Sin(angle) * Math.Sqrt(2);

        for (int i = 0; i + 1 < buffer.Length; i += 2)
        {
            buffer[i] = (float)(buffer[i] * lGain);
            buffer[i + 1] = (float)(buffer[i + 1] * rGain);
        }
        return buffer;
    }

    /// <summary>Lays one rendered block into a track buffer, with its gain and fades.</summary>
    private static void Place(float[] buffer, EditorProject project, TimelineItem item)
    {
        var audio = RenderItem(project, item);
        if (audio.Length == 0) return;

        var offset = Math.Clamp(ToSamples(item.Start), 0, Math.Max(0, buffer.Length - 1));
        var gain = (float)item.Gain;

        var fadeIn = Math.Min(ToSamples(item.FadeIn), audio.Length);
        var fadeOut = Math.Min(ToSamples(item.FadeOut), audio.Length - fadeIn);

        for (int i = 0; i < audio.Length; i++)
        {
            var dst = offset + i;
            if (dst >= buffer.Length) break;

            var g = gain;
            if (fadeIn > 0 && i < fadeIn) g *= (float)i / fadeIn;
            else if (fadeOut > 0 && i >= audio.Length - fadeOut) g *= (float)(audio.Length - i) / fadeOut;

            buffer[dst] += audio[i] * g;
        }
    }

    // ──────────────────────────────────────────────────────────
    //  DISPLAY HELPERS
    // ──────────────────────────────────────────────────────────
    public static string Time(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var m = (int)(seconds / 60);
        var s = seconds - m * 60;
        return $"{m}:{s:00.00}";
    }
}
