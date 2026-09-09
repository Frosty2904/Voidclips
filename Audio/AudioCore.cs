using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoidClip.Audio;

// ══════════════════════════════════════════════════════════════
//  CANONICAL FORMAT — everything in the app lives at 48k stereo float
// ══════════════════════════════════════════════════════════════
public static class Fmt
{
    public const int Rate = 48000;
    public const int Channels = 2;
    public static WaveFormat Wave => WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
}

// ══════════════════════════════════════════════════════════════
//  ROLLING BUFFER — lock-guarded circular store of recent audio
// ══════════════════════════════════════════════════════════════
public sealed class RollingBuffer
{
    private readonly object _lock = new();
    private float[] _buf = Array.Empty<float>();
    private int _write;
    private long _written;          // total samples ever written (interleaved)
    private bool _wrapped;

    public int SampleRate { get; private set; } = Fmt.Rate;
    public int Channels { get; private set; } = Fmt.Channels;
    public int CapacitySeconds { get; private set; }

    public void Configure(int sampleRate, int channels, int seconds)
    {
        lock (_lock)
        {
            SampleRate = sampleRate;
            Channels = Math.Max(1, channels);
            CapacitySeconds = seconds;
            var cap = (long)sampleRate * Channels * seconds;
            _buf = new float[Math.Max(1024, cap)];
            _write = 0;
            _written = 0;
            _wrapped = false;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buf);
            _write = 0; _written = 0; _wrapped = false;
        }
    }

    /// <summary>Seconds of audio currently held (grows until the buffer is full).</summary>
    public double Fill
    {
        get
        {
            lock (_lock)
            {
                if (_buf.Length == 0) return 0;
                var samples = _wrapped ? _buf.Length : _write;
                return (double)samples / (SampleRate * Channels);
            }
        }
    }

    public long TotalWritten { get { lock (_lock) return _written; } }

    public void Write(float[] data, int offset, int count)
    {
        if (count <= 0) return;
        lock (_lock)
        {
            if (_buf.Length == 0) return;
            if (count >= _buf.Length)
            {
                // incoming chunk larger than the whole ring — keep only its tail
                Array.Copy(data, offset + count - _buf.Length, _buf, 0, _buf.Length);
                _write = 0; _wrapped = true; _written += count;
                return;
            }
            var first = Math.Min(count, _buf.Length - _write);
            Array.Copy(data, offset, _buf, _write, first);
            var rest = count - first;
            if (rest > 0) Array.Copy(data, offset + first, _buf, 0, rest);
            _write = (_write + count) % _buf.Length;
            if (_write < first || rest > 0) _wrapped = true;
            if (!_wrapped && _write == 0) _wrapped = true;
            _written += count;
        }
    }

    public void WriteSilence(int count)
    {
        if (count <= 0) return;
        var zeros = new float[Math.Min(count, 48000)];
        var left = count;
        while (left > 0)
        {
            var n = Math.Min(left, zeros.Length);
            Write(zeros, 0, n);
            left -= n;
        }
    }

    /// <summary>
    /// Copies the most recent <paramref name="seconds"/> of audio, ending
    /// <paramref name="endOffsetSeconds"/> before the write head.
    /// </summary>
    public float[] Snapshot(double seconds, double endOffsetSeconds, out int rate, out int channels)
    {
        lock (_lock)
        {
            rate = SampleRate; channels = Channels;
            if (_buf.Length == 0) return Array.Empty<float>();

            var frameSize = Channels;
            var available = _wrapped ? _buf.Length : _write;

            var endBack = (int)(endOffsetSeconds * SampleRate) * frameSize;   // samples back from head
            var want = (int)(seconds * SampleRate) * frameSize;

            endBack = Math.Max(0, Math.Min(endBack, available));
            want = Math.Min(want, available - endBack);
            if (want <= 0) return Array.Empty<float>();

            var result = new float[want];
            // start index = write head - endBack - want
            var start = _write - endBack - want;
            start %= _buf.Length;
            if (start < 0) start += _buf.Length;

            var first = Math.Min(want, _buf.Length - start);
            Array.Copy(_buf, start, result, 0, first);
            if (want > first) Array.Copy(_buf, 0, result, first, want - first);
            return result;
        }
    }
}

// ══════════════════════════════════════════════════════════════
//  DEVICES
// ══════════════════════════════════════════════════════════════
public class AudioDeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public override string ToString() => IsDefault ? Name + "  (default)" : Name;
}

public static class AudioDevices
{
    public static List<AudioDeviceInfo> Render()
    {
        var list = new List<AudioDeviceInfo> { new() { Id = "", Name = "System default output" } };
        try
        {
            using var en = new MMDeviceEnumerator();
            string defId = null;
            try { defId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName, IsDefault = d.ID == defId });
        }
        catch { }
        return list;
    }

    public static List<AudioDeviceInfo> Capture()
    {
        var list = new List<AudioDeviceInfo> { new() { Id = "", Name = "System default input" } };
        try
        {
            using var en = new MMDeviceEnumerator();
            string defId = null;
            try { defId = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName, IsDefault = d.ID == defId });
        }
        catch { }
        return list;
    }

    public static MMDevice Get(string id, DataFlow flow)
    {
        var en = new MMDeviceEnumerator();
        if (string.IsNullOrEmpty(id))
            return en.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        try { return en.GetDevice(id); }
        catch { return en.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
    }

    public static string NameOf(string id, DataFlow flow)
    {
        if (string.IsNullOrEmpty(id)) return flow == DataFlow.Render ? "System default output" : "System default input";
        try { using var en = new MMDeviceEnumerator(); return en.GetDevice(id).FriendlyName; }
        catch { return "(missing device)"; }
    }
}

// ══════════════════════════════════════════════════════════════
//  SAMPLE PROVIDER OVER A FLOAT ARRAY
// ══════════════════════════════════════════════════════════════
public class ArraySampleProvider : ISampleProvider
{
    private readonly float[] _data;
    private int _pos;
    public WaveFormat WaveFormat { get; }

    public ArraySampleProvider(float[] data, int rate, int channels)
    {
        _data = data;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var n = Math.Min(count, _data.Length - _pos);
        if (n <= 0) return 0;
        Array.Copy(_data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }
}

// ══════════════════════════════════════════════════════════════
//  DSP HELPERS
// ══════════════════════════════════════════════════════════════
public static class Dsp
{
    /// <summary>Convert any interleaved buffer to canonical 48 kHz stereo.</summary>
    public static float[] ToCanonical(float[] input, int rate, int channels)
    {
        if (input.Length == 0) return input;
        channels = Math.Max(1, channels);

        // ── channel fold ──
        float[] stereo;
        if (channels == 2) stereo = input;
        else if (channels == 1)
        {
            stereo = new float[input.Length * 2];
            for (int i = 0; i < input.Length; i++) { stereo[i * 2] = input[i]; stereo[i * 2 + 1] = input[i]; }
        }
        else
        {
            var frames = input.Length / channels;
            stereo = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                // front L/R plus a folded-in centre when present
                float l = input[f * channels];
                float r = input[f * channels + 1];
                if (channels >= 3) { var c = input[f * channels + 2] * 0.707f; l += c; r += c; }
                stereo[f * 2] = Math.Clamp(l, -1f, 1f);
                stereo[f * 2 + 1] = Math.Clamp(r, -1f, 1f);
            }
        }

        if (rate == Fmt.Rate) return stereo;

        // ── resample ──
        var src = new ArraySampleProvider(stereo, rate, 2);
        var rs = new WdlResamplingSampleProvider(src, Fmt.Rate);
        var outLen = (int)((long)stereo.Length * Fmt.Rate / rate) + 4096;
        var outBuf = new float[outLen];
        int total = 0, read;
        while (total < outBuf.Length && (read = rs.Read(outBuf, total, Math.Min(16384, outBuf.Length - total))) > 0)
            total += read;
        if (total % 2 == 1) total--;
        var final = new float[total];
        Array.Copy(outBuf, final, total);
        return final;
    }

    public static float Peak(float[] data)
    {
        float p = 0;
        for (int i = 0; i < data.Length; i++) { var a = Math.Abs(data[i]); if (a > p) p = a; }
        return p;
    }

    public static float Rms(float[] data, int offset, int count)
    {
        if (count <= 0) return 0;
        double sum = 0;
        for (int i = offset; i < offset + count && i < data.Length; i++) sum += (double)data[i] * data[i];
        return (float)Math.Sqrt(sum / count);
    }

    public static void ApplyGain(float[] data, float gain)
    {
        if (Math.Abs(gain - 1f) < 0.0001f) return;
        for (int i = 0; i < data.Length; i++) data[i] = Math.Clamp(data[i] * gain, -1f, 1f);
    }

    /// <summary>Peak-normalise to the given dBFS target.</summary>
    public static void Normalize(float[] data, double targetDb)
    {
        var peak = Peak(data);
        if (peak < 0.00001f) return;
        var target = (float)Math.Pow(10, targetDb / 20.0);
        ApplyGain(data, target / peak);
    }

    public static void Fade(float[] data, int channels, int fadeInMs, int fadeOutMs, int rate)
    {
        if (data.Length == 0) return;
        var frames = data.Length / channels;

        var inF = Math.Min(frames, fadeInMs * rate / 1000);
        for (int f = 0; f < inF; f++)
        {
            var g = (float)f / inF;
            for (int c = 0; c < channels; c++) data[f * channels + c] *= g;
        }

        var outF = Math.Min(frames, fadeOutMs * rate / 1000);
        for (int f = 0; f < outF; f++)
        {
            var g = (float)f / outF;
            var idx = frames - 1 - f;
            for (int c = 0; c < channels; c++) data[idx * channels + c] *= g;
        }
    }

    /// <summary>
    /// Finds where the audible part of a range starts and ends, as absolute sample
    /// indices into <paramref name="data"/>. False when the whole range is silence.
    /// </summary>
    public static bool SilenceBounds(float[] data, int offset, int count, int channels, int rate,
                                     double thresholdDb, int padMs, out int first, out int last)
    {
        first = offset;
        last = offset + count;
        if (count <= 0 || channels <= 0) return false;

        var thr = (float)Math.Pow(10, thresholdDb / 20.0);
        var frames = count / channels;
        var win = Math.Max(1, rate / 100);   // 10 ms analysis window

        int firstF = -1, lastF = -1;
        for (int f = 0; f < frames; f += win)
        {
            var n = Math.Min(win, frames - f) * channels;
            if (Rms(data, offset + f * channels, n) > thr) { if (firstF < 0) firstF = f; lastF = f + win; }
        }
        if (firstF < 0) return false;

        var pad = padMs * rate / 1000;
        firstF = Math.Max(0, firstF - pad);
        lastF = Math.Min(frames, lastF + pad);

        first = offset + firstF * channels;
        last = offset + lastF * channels;
        return last > first;
    }

    /// <summary>Strip leading/trailing audio below the threshold, keeping a small pad.</summary>
    public static float[] TrimSilence(float[] data, int channels, int rate, double thresholdDb, int padMs = 120)
    {
        if (data.Length == 0) return data;
        if (!SilenceBounds(data, 0, data.Length, channels, rate, thresholdDb, padMs, out var first, out var last))
            return data;   // all silent — leave untouched

        var outLen = last - first;
        if (outLen <= 0 || outLen >= data.Length) return data;
        var res = new float[outLen];
        Array.Copy(data, first, res, 0, outLen);
        return res;
    }

    /// <summary>Downsample to per-bucket peaks for waveform drawing.</summary>
    public static float[] Peaks(float[] data, int channels, int buckets)
    {
        var res = new float[buckets];
        if (data.Length == 0) return res;
        var frames = data.Length / channels;
        var per = Math.Max(1, frames / buckets);
        for (int b = 0; b < buckets; b++)
        {
            var start = b * per;
            if (start >= frames) break;
            var end = Math.Min(frames, start + per);
            float p = 0;
            for (int f = start; f < end; f++)
                for (int c = 0; c < channels; c++)
                {
                    var a = Math.Abs(data[f * channels + c]);
                    if (a > p) p = a;
                }
            res[b] = Math.Min(1f, p);
        }
        return res;
    }
}
