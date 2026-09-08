using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoidClip.Models;

namespace VoidClip.Audio;

/// <summary>A single firing of a clip on one output.</summary>
public sealed class ClipVoice : ISampleProvider
{
    private readonly float[] _data;
    private int _pos;
    private volatile bool _stop;

    public string ClipId { get; }
    public bool Loop { get; set; }
    public float Volume { get; set; } = 1f;
    public WaveFormat WaveFormat => Fmt.Wave;
    public double Position => _data.Length == 0 ? 0 : (double)_pos / (Fmt.Rate * Fmt.Channels);
    public double Length => (double)_data.Length / (Fmt.Rate * Fmt.Channels);

    public ClipVoice(string clipId, float[] data) { ClipId = clipId; _data = data ?? Array.Empty<float>(); }

    public void Stop() => _stop = true;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_stop) return 0;
        var written = 0;
        while (written < count)
        {
            if (_pos >= _data.Length)
            {
                if (Loop && _data.Length > 0) _pos = 0;
                else break;
            }
            var n = Math.Min(count - written, _data.Length - _pos);
            var v = Volume;
            for (int i = 0; i < n; i++) buffer[offset + written + i] = _data[_pos + i] * v;
            _pos += n;
            written += n;
        }
        return written;
    }
}

/// <summary>One WASAPI output plus its mixer.</summary>
internal sealed class Rig : IDisposable
{
    public WasapiOut Out;
    public MixingSampleProvider Mixer;
    public VolumeSampleProvider Vol;
    public string DeviceId = "";
    public string ResolvedId = "";
    public int LatencyMs;
    public string Error;
    public string DeviceName = "";

    public bool Ok => Out != null && Error == null;

    public void Dispose()
    {
        try { Out?.Stop(); } catch { }
        try { Out?.Dispose(); } catch { }
        Out = null; Mixer = null; Vol = null;
    }
}

public sealed class PlaybackEngine : IDisposable
{
    private readonly object _sync = new();
    private Rig _primary = new();
    private Rig _monitor = new();
    private readonly Dictionary<string, List<ClipVoice>> _active = new();

    public event Action<string> ClipStarted;
    public event Action<string> ClipStopped;
    public event Action<string> Problem;

    public bool MonitorEnabled { get; private set; } = true;

    /// <summary>
    /// True when the monitor resolves to the same endpoint as the main output.
    /// Playing both would just double the level, so the monitor is skipped.
    /// </summary>
    public bool MonitorSuppressed { get; private set; }

    private bool MonitorActive => MonitorEnabled && !MonitorSuppressed && _monitor.Ok;
    public bool AllowOverlap { get; private set; } = true;
    public bool StopPreviousOnPlay { get; private set; }
    public bool RestartOnRetrigger { get; private set; } = true;

    public string PrimaryStatus => _primary.Ok ? _primary.DeviceName : (_primary.Error ?? "not started");
    public string MonitorStatus => !MonitorEnabled ? "off"
        : MonitorSuppressed ? "same device as the main output — skipped"
        : _monitor.Ok ? _monitor.DeviceName
        : (_monitor.Error ?? "not started");

    // ──────────────────────────────────────────────────────────
    public void Configure(AppSettings s)
    {
        lock (_sync)
        {
            MonitorEnabled = s.MonitorEnabled;
            AllowOverlap = s.AllowOverlap;
            StopPreviousOnPlay = s.StopPreviousOnPlay;
            RestartOnRetrigger = s.RestartOnRetrigger;

            EnsureRig(ref _primary, s.OutputDeviceId, s.PlaybackLatencyMs);
            if (s.MonitorEnabled) EnsureRig(ref _monitor, s.MonitorDeviceId, s.PlaybackLatencyMs);
            else { _monitor.Dispose(); _monitor = new Rig(); }

            MonitorSuppressed = s.MonitorEnabled && _monitor.Ok && _primary.Ok
                                && _monitor.ResolvedId == _primary.ResolvedId;

            if (_primary.Vol != null) _primary.Vol.Volume = (float)s.MasterVolume;
            if (_monitor.Vol != null) _monitor.Vol.Volume = (float)s.MonitorVolume;
        }
    }

    public void SetVolumes(double master, double monitor)
    {
        lock (_sync)
        {
            if (_primary.Vol != null) _primary.Vol.Volume = (float)master;
            if (_monitor.Vol != null) _monitor.Vol.Volume = (float)monitor;
        }
    }

    private void EnsureRig(ref Rig rig, string deviceId, int latency)
    {
        if (rig.Ok && rig.DeviceId == deviceId && rig.LatencyMs == latency) return;

        rig.Dispose();
        var fresh = new Rig { DeviceId = deviceId, LatencyMs = latency };
        try
        {
            var dev = AudioDevices.Get(deviceId, DataFlow.Render);
            fresh.DeviceName = dev.FriendlyName;
            fresh.ResolvedId = dev.ID;
            fresh.Mixer = new MixingSampleProvider(Fmt.Wave) { ReadFully = true };
            fresh.Mixer.MixerInputEnded += OnInputEnded;
            fresh.Vol = new VolumeSampleProvider(fresh.Mixer);
            fresh.Out = new WasapiOut(dev, AudioClientShareMode.Shared, true, latency);
            fresh.Out.Init(fresh.Vol);
            fresh.Out.Play();
        }
        catch (Exception ex)
        {
            fresh.Error = ex.Message;
            fresh.Out = null;
            Problem?.Invoke($"Output '{AudioDevices.NameOf(deviceId, DataFlow.Render)}' unavailable: {ex.Message}");
        }
        rig = fresh;
    }

    // ──────────────────────────────────────────────────────────
    public void Play(string clipId, float[] samples, float volume, bool loop)
    {
        if (samples == null || samples.Length == 0) return;

        lock (_sync)
        {
            if (StopPreviousOnPlay) StopAllInternal();
            else if (_active.ContainsKey(clipId))
            {
                if (RestartOnRetrigger) StopClipInternal(clipId);
                else if (!AllowOverlap) return;
            }
            else if (!AllowOverlap) StopAllInternal();

            var voices = new List<ClipVoice>(2);
            if (_primary.Ok) voices.Add(AddVoice(_primary, clipId, samples, volume, loop));
            if (MonitorActive) voices.Add(AddVoice(_monitor, clipId, samples, volume, loop));

            if (voices.Count == 0)
            {
                Problem?.Invoke("No working output device — check Settings ▸ Playback.");
                return;
            }

            if (!_active.TryGetValue(clipId, out var list)) _active[clipId] = list = new List<ClipVoice>();
            list.AddRange(voices);
        }
        ClipStarted?.Invoke(clipId);
    }

    private ClipVoice AddVoice(Rig rig, string clipId, float[] samples, float volume, bool loop)
    {
        var v = new ClipVoice(clipId, samples) { Volume = volume, Loop = loop };
        rig.Mixer.AddMixerInput((ISampleProvider)v);
        return v;
    }

    /// <summary>Editor preview — headphones only, never the virtual cable.</summary>
    public ClipVoice Preview(float[] samples, float volume = 1f)
    {
        if (samples == null || samples.Length == 0) return null;
        lock (_sync)
        {
            StopClipInternal("__preview__");
            var rig = MonitorActive ? _monitor : _primary;
            if (!rig.Ok) { Problem?.Invoke("No output device available for preview."); return null; }
            var v = new ClipVoice("__preview__", samples) { Volume = volume };
            rig.Mixer.AddMixerInput((ISampleProvider)v);
            _active["__preview__"] = new List<ClipVoice> { v };
            return v;
        }
    }

    public void StopPreview() { lock (_sync) StopClipInternal("__preview__"); }

    public void StopClip(string clipId)
    {
        lock (_sync) StopClipInternal(clipId);
        ClipStopped?.Invoke(clipId);
    }

    public void StopAll()
    {
        List<string> ids;
        lock (_sync) { ids = _active.Keys.ToList(); StopAllInternal(); }
        foreach (var id in ids) ClipStopped?.Invoke(id);
    }

    public bool IsPlaying(string clipId) { lock (_sync) return _active.ContainsKey(clipId); }

    public int ActiveCount { get { lock (_sync) return _active.Count(k => k.Key != "__preview__"); } }

    private void StopClipInternal(string clipId)
    {
        if (!_active.TryGetValue(clipId, out var list)) return;
        foreach (var v in list) v.Stop();
        _active.Remove(clipId);
    }

    private void StopAllInternal()
    {
        foreach (var kv in _active)
            foreach (var v in kv.Value) v.Stop();
        _active.Clear();
    }

    private void OnInputEnded(object sender, SampleProviderEventArgs e)
    {
        if (e.SampleProvider is not ClipVoice voice) return;
        string finished = null;
        lock (_sync)
        {
            if (_active.TryGetValue(voice.ClipId, out var list))
            {
                list.Remove(voice);
                if (list.Count == 0) { _active.Remove(voice.ClipId); finished = voice.ClipId; }
            }
        }
        if (finished != null && finished != "__preview__") ClipStopped?.Invoke(finished);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            StopAllInternal();
            _primary.Dispose();
            _monitor.Dispose();
        }
    }
}
