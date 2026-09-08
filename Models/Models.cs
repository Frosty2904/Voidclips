using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace VoidClip.Models;

public class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void Raise([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

// ══════════════════════════════════════════════════════════════
//  CLIP
// ══════════════════════════════════════════════════════════════
public class Clip : Bindable
{
    private string _name = "Untitled";
    private string _categoryId = "";
    private double _volume = 1.0;
    private string _hotkey = "";
    private bool _loop;
    private string _color = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>File name (not full path) inside the clips folder.</summary>
    public string FileName { get; set; } = "";

    public string CategoryId { get => _categoryId; set => Set(ref _categoryId, value); }

    public DateTime Created { get; set; } = DateTime.Now;

    public double Duration { get; set; }

    /// <summary>Per-clip playback gain, 0..2.</summary>
    public double Volume { get => _volume; set => Set(ref _volume, Math.Clamp(value, 0, 2)); }

    /// <summary>Global hotkey gesture for this pad, e.g. "Ctrl+Alt+D1". Empty = none.</summary>
    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value); }

    public bool Loop { get => _loop; set => Set(ref _loop, value); }

    /// <summary>Optional per-clip accent override (#RRGGBB). Empty = use category colour.</summary>
    public string Color { get => _color; set => Set(ref _color, value); }

    public int PlayCount { get; set; }

    public bool Favorite { get; set; }

    /// <summary>Cached waveform peaks (0..1) for pad rendering.</summary>
    public float[] Peaks { get; set; }

    [JsonIgnore] public string DurationText => Duration >= 60
        ? $"{(int)(Duration / 60)}:{(int)(Duration % 60):00}"
        : $"{Duration:0.0}s";

    [JsonIgnore]
    private bool _isPlaying;
    [JsonIgnore]
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    /// <summary>Board selection, for acting on several clips at once. Never persisted.</summary>
    [JsonIgnore]
    private bool _isSelected;
    [JsonIgnore]
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public void NotifyAll()
    {
        Raise(nameof(Name)); Raise(nameof(DurationText)); Raise(nameof(Hotkey));
        Raise(nameof(CategoryId)); Raise(nameof(Volume)); Raise(nameof(Color));
    }
}

// ══════════════════════════════════════════════════════════════
//  CATEGORY
// ══════════════════════════════════════════════════════════════
public class ClipCategory : Bindable
{
    private string _name = "New Category";
    private string _color = "#8B3DFF";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Color { get => _color; set => Set(ref _color, value); }
    public int Order { get; set; }

    [JsonIgnore]
    private int _count;
    [JsonIgnore]
    public int Count { get => _count; set => Set(ref _count, value); }

    [JsonIgnore]
    private bool _isSelected;
    [JsonIgnore]
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

// ══════════════════════════════════════════════════════════════
//  SETTINGS
// ══════════════════════════════════════════════════════════════
public enum CaptureSource { Loopback, InputDevice }
public enum ExportFormat { Wav, Mp3, Ogg }
public enum PadSize { Small, Medium, Large }
public enum SortMode { NewestFirst, OldestFirst, NameAsc, NameDesc, MostPlayed, LongestFirst }

public class AppSettings : Bindable
{
    // ─────────── CAPTURE ───────────
    private CaptureSource _captureMode = CaptureSource.Loopback;
    private string _loopbackDeviceId = "";
    private string _inputDeviceId = "";
    private int _bufferSeconds = 120;
    private bool _autoStartCapture = true;
    private double _captureGain = 1.0;

    public CaptureSource CaptureSource { get => _captureMode; set => Set(ref _captureMode, value); }
    /// <summary>Render device whose output is loopback-captured. Empty = default render device.</summary>
    public string LoopbackDeviceId { get => _loopbackDeviceId; set => Set(ref _loopbackDeviceId, value); }
    /// <summary>Capture device used when CaptureSource is InputDevice. Empty = default capture device.</summary>
    public string InputDeviceId { get => _inputDeviceId; set => Set(ref _inputDeviceId, value); }
    /// <summary>Rolling history length in seconds (15..900).</summary>
    public int BufferSeconds { get => _bufferSeconds; set => Set(ref _bufferSeconds, Math.Clamp(value, 15, 900)); }
    public bool AutoStartCapture { get => _autoStartCapture; set => Set(ref _autoStartCapture, value); }
    public double CaptureGain { get => _captureGain; set => Set(ref _captureGain, Math.Clamp(value, 0.1, 4.0)); }

    // ─────────── CLIPPING ───────────
    private double _defaultClipSeconds = 15;
    private int _reactionOffsetMs = 250;
    private bool _openEditorAfterClip;
    private bool _normalizeOnClip = true;
    private double _normalizeTargetDb = -1.5;
    private bool _trimSilence;
    private double _silenceThresholdDb = -45;
    private int _fadeInMs = 8;
    private int _fadeOutMs = 40;
    private string _nameTemplate = "Clip {n}";
    private string _defaultCategoryId = "";

    public double DefaultClipSeconds { get => _defaultClipSeconds; set => Set(ref _defaultClipSeconds, Math.Clamp(value, 0.5, 900)); }
    public List<double> QuickLengths { get; set; } = new() { 5, 10, 15, 30, 60 };
    /// <summary>Trims this much off the tail to compensate for hotkey reaction lag.</summary>
    public int ReactionOffsetMs { get => _reactionOffsetMs; set => Set(ref _reactionOffsetMs, Math.Clamp(value, 0, 5000)); }
    public bool OpenEditorAfterClip { get => _openEditorAfterClip; set => Set(ref _openEditorAfterClip, value); }
    public bool NormalizeOnClip { get => _normalizeOnClip; set => Set(ref _normalizeOnClip, value); }
    public double NormalizeTargetDb { get => _normalizeTargetDb; set => Set(ref _normalizeTargetDb, Math.Clamp(value, -24, 0)); }
    public bool TrimSilence { get => _trimSilence; set => Set(ref _trimSilence, value); }
    public double SilenceThresholdDb { get => _silenceThresholdDb; set => Set(ref _silenceThresholdDb, Math.Clamp(value, -80, -10)); }
    public int FadeInMs { get => _fadeInMs; set => Set(ref _fadeInMs, Math.Clamp(value, 0, 2000)); }
    public int FadeOutMs { get => _fadeOutMs; set => Set(ref _fadeOutMs, Math.Clamp(value, 0, 5000)); }
    public string NameTemplate { get => _nameTemplate; set => Set(ref _nameTemplate, value); }
    public string DefaultCategoryId { get => _defaultCategoryId; set => Set(ref _defaultCategoryId, value); }

    // ─────────── PLAYBACK ───────────
    private string _outputDeviceId = "";
    private string _monitorDeviceId = "";
    private bool _monitorEnabled;
    private double _masterVolume = 0.85;
    private double _monitorVolume = 0.6;
    private bool _allowOverlap = true;
    private bool _stopPreviousOnPlay;
    private int _playbackLatencyMs = 60;
    private bool _restartOnRetrigger = true;

    /// <summary>Primary output — point this at a virtual cable that Discord uses as its mic.</summary>
    public string OutputDeviceId { get => _outputDeviceId; set => Set(ref _outputDeviceId, value); }
    /// <summary>
    /// Secondary output so you hear what you fire (usually your headphones).
    /// Off by default — it only helps once the main output is a separate virtual cable.
    /// </summary>
    public string MonitorDeviceId { get => _monitorDeviceId; set => Set(ref _monitorDeviceId, value); }
    public bool MonitorEnabled { get => _monitorEnabled; set => Set(ref _monitorEnabled, value); }
    public double MasterVolume { get => _masterVolume; set => Set(ref _masterVolume, Math.Clamp(value, 0, 1.5)); }
    public double MonitorVolume { get => _monitorVolume; set => Set(ref _monitorVolume, Math.Clamp(value, 0, 1.5)); }
    public bool AllowOverlap { get => _allowOverlap; set => Set(ref _allowOverlap, value); }
    public bool StopPreviousOnPlay { get => _stopPreviousOnPlay; set => Set(ref _stopPreviousOnPlay, value); }
    public int PlaybackLatencyMs { get => _playbackLatencyMs; set => Set(ref _playbackLatencyMs, Math.Clamp(value, 20, 400)); }
    public bool RestartOnRetrigger { get => _restartOnRetrigger; set => Set(ref _restartOnRetrigger, value); }

    // ─────────── HOTKEYS ───────────
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["ClipNow"] = "Ctrl+Alt+C",
        ["ClipQuick1"] = "Ctrl+Alt+D1",
        ["ClipQuick2"] = "Ctrl+Alt+D2",
        ["ClipQuick3"] = "Ctrl+Alt+D3",
        ["ClipQuick4"] = "Ctrl+Alt+D4",
        ["ClipQuick5"] = "Ctrl+Alt+D5",
        ["StopAll"] = "Ctrl+Alt+X",
        ["ToggleCapture"] = "Ctrl+Alt+R",
        ["ShowHide"] = "Ctrl+Alt+V",
        ["ReplayLast"] = "Ctrl+Alt+Z",
    };

    private bool _hotkeysEnabled = true;
    public bool HotkeysEnabled { get => _hotkeysEnabled; set => Set(ref _hotkeysEnabled, value); }

    // ─────────── EXPORT ───────────
    private ExportFormat _defaultExportFormat = ExportFormat.Mp3;
    private int _mp3Bitrate = 192;
    private float _oggQuality = 0.5f;
    private string _exportFolder = "";
    private bool _openFolderAfterExport = true;

    public ExportFormat DefaultExportFormat { get => _defaultExportFormat; set => Set(ref _defaultExportFormat, value); }
    public int Mp3Bitrate { get => _mp3Bitrate; set => Set(ref _mp3Bitrate, value); }
    /// <summary>Vorbis quality, 0..1 (maps to -q 0..10).</summary>
    public float OggQuality { get => _oggQuality; set => Set(ref _oggQuality, Math.Clamp(value, 0f, 1f)); }
    public string ExportFolder { get => _exportFolder; set => Set(ref _exportFolder, value); }
    public bool OpenFolderAfterExport { get => _openFolderAfterExport; set => Set(ref _openFolderAfterExport, value); }

    // ─────────── INTERFACE ───────────
    private PadSize _padSize = PadSize.Medium;
    private bool _showWaveformOnPads = true;
    private bool _minimizeToTray = true;
    private bool _startMinimized;
    private bool _alwaysOnTop;
    private bool _showToastOnClip = true;
    private SortMode _sortMode = SortMode.NewestFirst;
    private double _backdropIntensity = 1.0;
    private bool _animations = true;
    private bool _confirmDelete = true;

    public PadSize PadSize { get => _padSize; set => Set(ref _padSize, value); }
    public bool ShowWaveformOnPads { get => _showWaveformOnPads; set => Set(ref _showWaveformOnPads, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => Set(ref _minimizeToTray, value); }
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }
    public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }
    public bool ShowToastOnClip { get => _showToastOnClip; set => Set(ref _showToastOnClip, value); }
    public SortMode SortMode { get => _sortMode; set => Set(ref _sortMode, value); }
    public double BackdropIntensity { get => _backdropIntensity; set => Set(ref _backdropIntensity, Math.Clamp(value, 0, 1.5)); }
    public bool Animations { get => _animations; set => Set(ref _animations, value); }
    public bool ConfirmDelete { get => _confirmDelete; set => Set(ref _confirmDelete, value); }

    // ─────────── UPDATES ───────────
    private bool _autoCheckUpdates = true;
    private bool _autoInstallUpdates = true;
    private int _updateCheckHours = 6;

    /// <summary>Look at the GitHub repo on launch and periodically after that.</summary>
    public bool AutoCheckUpdates { get => _autoCheckUpdates; set => Set(ref _autoCheckUpdates, value); }

    /// <summary>
    /// Download and swap in a new release without asking. The swap only takes effect
    /// on the next launch, so this never restarts the app underneath you mid-call.
    /// </summary>
    public bool AutoInstallUpdates { get => _autoInstallUpdates; set => Set(ref _autoInstallUpdates, value); }

    public int UpdateCheckHours { get => _updateCheckHours; set => Set(ref _updateCheckHours, Math.Clamp(value, 1, 168)); }

    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>Release tag already installed and waiting for a restart. Empty when none.</summary>
    public string PendingUpdateVersion { get; set; } = "";

    /// <summary>Release tag the user chose to skip, so they stop being told about it.</summary>
    public string SkippedUpdateTag { get; set; } = "";

    // ─────────── STORAGE ───────────
    private string _clipsFolder = "";
    private bool _runAtStartup;

    /// <summary>Empty = %AppData%\VoidClip\Clips</summary>
    public string ClipsFolder { get => _clipsFolder; set => Set(ref _clipsFolder, value); }
    public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }

    // window placement
    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 810;
    /// <summary>Null until the window has been placed at least once.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
}

// ══════════════════════════════════════════════════════════════
//  LIBRARY FILE
// ══════════════════════════════════════════════════════════════
public class LibraryFile
{
    public int Version { get; set; } = 1;
    public List<Clip> Clips { get; set; } = new();
    public List<ClipCategory> Categories { get; set; } = new();
}
