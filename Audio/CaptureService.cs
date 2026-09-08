using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoidClip.Models;

namespace VoidClip.Audio;

/// <summary>
/// Keeps a rolling window of everything coming out of (or into) a chosen device,
/// so any moment in the recent past can be grabbed instantly.
/// </summary>
public sealed class CaptureService : IDisposable
{
    private readonly RollingBuffer _buffer = new();
    private readonly object _sync = new();

    private WasapiCapture _capture;
    private Stopwatch _clock;
    private System.Threading.Timer _idleTimer;
    private float[] _scratch = new float[16384];
    private bool _stopRequested;
    private int _restartAttempts;

    public bool IsCapturing { get; private set; }
    public string DeviceName { get; private set; } = "";
    public int SampleRate => _buffer.SampleRate;
    public int Channels => _buffer.Channels;
    public double BufferFill => _buffer.Fill;
    public int BufferCapacitySeconds => _buffer.CapacitySeconds;
    public float CaptureGain { get; set; } = 1f;

    public event Action<float> LevelChanged;          // 0..1 peak
    public event Action<bool, string> StatusChanged;  // running, message

    // ──────────────────────────────────────────────────────────
    public void Start(AppSettings s)
    {
        Stop();
        lock (_sync)
        {
            _stopRequested = false;
            CaptureGain = (float)s.CaptureGain;
            try
            {
                MMDevice dev;
                if (s.CaptureSource == CaptureSource.Loopback)
                {
                    dev = AudioDevices.Get(s.LoopbackDeviceId, DataFlow.Render);
                    _capture = new WasapiLoopbackCapture(dev);
                }
                else
                {
                    dev = AudioDevices.Get(s.InputDeviceId, DataFlow.Capture);
                    _capture = new WasapiCapture(dev);
                }
                DeviceName = dev.FriendlyName;

                var wf = _capture.WaveFormat;
                _buffer.Configure(wf.SampleRate, wf.Channels, s.BufferSeconds);

                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += OnStopped;
                _clock = Stopwatch.StartNew();
                _capture.StartRecording();
                IsCapturing = true;
                _restartAttempts = 0;

                _idleTimer = new System.Threading.Timer(_ => PadIdle(), null, 250, 250);
                StatusChanged?.Invoke(true, DeviceName);
            }
            catch (Exception ex)
            {
                IsCapturing = false;
                StatusChanged?.Invoke(false, "Capture failed: " + ex.Message);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _stopRequested = true;
            _idleTimer?.Dispose(); _idleTimer = null;
            if (_capture != null)
            {
                try { _capture.DataAvailable -= OnData; _capture.RecordingStopped -= OnStopped; } catch { }
                try { _capture.StopRecording(); } catch { }
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }
            _clock = null;
            IsCapturing = false;
        }
        StatusChanged?.Invoke(false, "Stopped");
    }

    public void ClearBuffer() => _buffer.Clear();

    /// <summary>Reconfigure history length without dropping the capture stream.</summary>
    public void ResizeBuffer(int seconds)
    {
        lock (_sync)
        {
            if (!IsCapturing) return;
            _buffer.Configure(_buffer.SampleRate, _buffer.Channels, seconds);
            _clock?.Restart();
        }
    }

    // ──────────────────────────────────────────────────────────
    private void OnData(object sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var wf = _capture?.WaveFormat;
        if (wf == null) return;

        var count = BytesToFloat(e.Buffer, e.BytesRecorded, wf, ref _scratch);
        if (count <= 0) return;

        if (Math.Abs(CaptureGain - 1f) > 0.001f)
            for (int i = 0; i < count; i++) _scratch[i] = Math.Clamp(_scratch[i] * CaptureGain, -1f, 1f);

        float peak = 0;
        for (int i = 0; i < count; i++) { var a = Math.Abs(_scratch[i]); if (a > peak) peak = a; }

        lock (_sync)
        {
            PadTo(_buffer.TotalWritten, count);
            _buffer.Write(_scratch, 0, count);
        }

        LevelChanged?.Invoke(peak);
    }

    /// <summary>
    /// WASAPI stays silent when the endpoint is idle. Insert the missing silence so
    /// "the last 15 seconds" always means 15 seconds of wall clock.
    /// </summary>
    private void PadTo(long have, int incoming)
    {
        if (_clock == null) return;
        var frameRate = (long)_buffer.SampleRate * _buffer.Channels;
        var expected = (long)(_clock.Elapsed.TotalSeconds * frameRate);
        var gap = expected - have - incoming;
        var threshold = frameRate / 12;   // ~80 ms
        if (gap > threshold)
        {
            var maxGap = frameRate * _buffer.CapacitySeconds;
            _buffer.WriteSilence((int)Math.Min(gap, maxGap));
        }
    }

    private void PadIdle()
    {
        lock (_sync)
        {
            if (!IsCapturing || _clock == null) return;
            PadTo(_buffer.TotalWritten, 0);
        }
    }

    private void OnStopped(object sender, StoppedEventArgs e)
    {
        if (_stopRequested) return;
        IsCapturing = false;
        StatusChanged?.Invoke(false, e.Exception != null ? "Device lost: " + e.Exception.Message : "Device stopped");
        _restartAttempts++;
        if (_restartAttempts <= 5) StatusChanged?.Invoke(false, "Reconnecting…");
    }

    // ──────────────────────────────────────────────────────────
    /// <summary>Grab the tail of the buffer and convert it to canonical 48k stereo.</summary>
    public float[] Grab(double seconds, double endOffsetSeconds)
    {
        float[] raw;
        int rate, ch;
        lock (_sync) raw = _buffer.Snapshot(seconds, endOffsetSeconds, out rate, out ch);
        if (raw.Length == 0) return raw;
        return Dsp.ToCanonical(raw, rate, ch);
    }

    // ──────────────────────────────────────────────────────────
    private static int BytesToFloat(byte[] src, int bytes, WaveFormat wf, ref float[] dst)
    {
        int samples;
        switch (wf.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat when wf.BitsPerSample == 32:
                samples = bytes / 4;
                EnsureSize(ref dst, samples);
                Buffer.BlockCopy(src, 0, dst, 0, samples * 4);
                return samples;

            case WaveFormatEncoding.Pcm when wf.BitsPerSample == 16:
                samples = bytes / 2;
                EnsureSize(ref dst, samples);
                for (int i = 0; i < samples; i++)
                    dst[i] = BitConverter.ToInt16(src, i * 2) / 32768f;
                return samples;

            case WaveFormatEncoding.Pcm when wf.BitsPerSample == 24:
                samples = bytes / 3;
                EnsureSize(ref dst, samples);
                for (int i = 0; i < samples; i++)
                {
                    int v = (src[i * 3] << 8) | (src[i * 3 + 1] << 16) | (src[i * 3 + 2] << 24);
                    dst[i] = (v >> 8) / 8388608f;
                }
                return samples;

            case WaveFormatEncoding.Pcm when wf.BitsPerSample == 32:
                samples = bytes / 4;
                EnsureSize(ref dst, samples);
                for (int i = 0; i < samples; i++)
                    dst[i] = BitConverter.ToInt32(src, i * 4) / 2147483648f;
                return samples;

            default:
                // Extensible formats usually resolve to float32 in shared mode.
                if (wf.BitsPerSample == 32)
                {
                    samples = bytes / 4;
                    EnsureSize(ref dst, samples);
                    Buffer.BlockCopy(src, 0, dst, 0, samples * 4);
                    return samples;
                }
                return 0;
        }
    }

    private static void EnsureSize(ref float[] buf, int needed)
    {
        if (buf.Length < needed) buf = new float[Math.Max(needed, buf.Length * 2)];
    }

    public void Dispose() => Stop();
}
