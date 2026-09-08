using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoidClip.Audio;
using VoidClip.Models;
using VoidClip.Services;

namespace VoidClip.Views;

public partial class SettingsWindow : Window
{
    private bool _loading = true;

    /// <summary>
    /// Slider ValueChanged fires while the XAML is still loading — before the named
    /// fields are assigned — so every handler that touches a control waits for this.
    /// </summary>
    private bool _built;
    private AppSettings S => Core.Settings;
    private MainWindow Main => Owner as MainWindow;

    /// <summary>The global actions users can rebind, in the order they're shown.</summary>
    private static readonly (string Action, string Label, string Hint)[] Actions =
    {
        ("ClipNow",       "Clip now",            "Grabs the default length from the buffer"),
        ("ClipQuick1",    "Quick length 1",      "First button next to CLIP"),
        ("ClipQuick2",    "Quick length 2",      "Second button"),
        ("ClipQuick3",    "Quick length 3",      "Third button"),
        ("ClipQuick4",    "Quick length 4",      "Fourth button"),
        ("ClipQuick5",    "Quick length 5",      "Fifth button"),
        ("ReplayLast",    "Replay last clip",    "Fires whatever you captured or played most recently"),
        ("StopAll",       "Stop all sounds",     "Panic button — kills every playing pad"),
        ("ToggleCapture", "Pause / resume capture", "Stops buffering without closing the app"),
        ("ShowHide",      "Show / hide window",  "Bring the board up over a game"),
    };

    public SettingsWindow()
    {
        InitializeComponent();
        KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += OnLoaded;
        _built = true;
    }

    // ══════════════════════════════════════════════════════════
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        Populate();
        Wire();
        BuildHotkeyRows();
        _loading = false;
        UpdateStatusLines();
    }

    private void Populate()
    {
        // ── capture ──
        ModeBox.SelectedIndex = S.CaptureSource == CaptureSource.Loopback ? 0 : 1;
        FillDevices(LoopbackBox, AudioDevices.Render(), S.LoopbackDeviceId);
        FillDevices(InputBox, AudioDevices.Capture(), S.InputDeviceId);
        LoopbackRow.Visibility = S.CaptureSource == CaptureSource.Loopback ? Visibility.Visible : Visibility.Collapsed;
        InputRow.Visibility = S.CaptureSource == CaptureSource.Loopback ? Visibility.Collapsed : Visibility.Visible;
        BufferSlider.Value = S.BufferSeconds;
        GainSlider.Value = S.CaptureGain;
        AutoStartChk.IsChecked = S.AutoStartCapture;

        // ── clipping ──
        ClipLenSlider.Value = Math.Clamp(S.DefaultClipSeconds, 1, 120);
        QuickBox.Text = string.Join(", ", S.QuickLengths.Select(v => v.ToString("0.#")));
        OffsetSlider.Value = S.ReactionOffsetMs;
        NormaliseChk.IsChecked = S.NormalizeOnClip;
        NormSlider.Value = S.NormalizeTargetDb;
        TrimChk.IsChecked = S.TrimSilence;
        SilenceSlider.Value = S.SilenceThresholdDb;
        FadeInSlider.Value = S.FadeInMs;
        FadeOutSlider.Value = S.FadeOutMs;
        TemplateBox.Text = S.NameTemplate;
        OpenEditorChk.IsChecked = S.OpenEditorAfterClip;

        DefaultCatBox.Items.Clear();
        DefaultCatBox.Items.Add(new ComboBoxItem { Content = "Uncategorised", Tag = "" });
        foreach (var c in Core.Library.Categories)
            DefaultCatBox.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
        DefaultCatBox.SelectedIndex = Math.Max(0,
            Core.Library.Categories.ToList().FindIndex(c => c.Id == S.DefaultCategoryId) + 1);

        // ── playback ──
        FillDevices(OutputBox, AudioDevices.Render(), S.OutputDeviceId);
        FillDevices(MonitorBox, AudioDevices.Render(), S.MonitorDeviceId);
        MonitorChk.IsChecked = S.MonitorEnabled;
        MonitorBox.IsEnabled = S.MonitorEnabled;
        MasterSlider.Value = S.MasterVolume;
        MonitorSlider.Value = S.MonitorVolume;
        LatencySlider.Value = S.PlaybackLatencyMs;
        OverlapChk.IsChecked = S.AllowOverlap;
        StopPrevChk.IsChecked = S.StopPreviousOnPlay;
        RetriggerChk.IsChecked = S.RestartOnRetrigger;
        HotkeysChk.IsChecked = S.HotkeysEnabled;

        // ── export ──
        FormatBox.SelectedIndex = (int)S.DefaultExportFormat;
        BitrateBox.SelectedIndex = S.Mp3Bitrate switch
        {
            96 => 0, 128 => 1, 160 => 2, 192 => 3, 256 => 4, 320 => 5, _ => 3
        };
        OggSlider.Value = S.OggQuality;
        RevealChk.IsChecked = S.OpenFolderAfterExport;
        ExportPathLabel.Text = S.ExportFolder;

        // ── interface ──
        PadSizeBox.SelectedIndex = (int)S.PadSize;
        WaveChk.IsChecked = S.ShowWaveformOnPads;
        AnimChk.IsChecked = S.Animations;
        ToastChk.IsChecked = S.ShowToastOnClip;
        ConfirmChk.IsChecked = S.ConfirmDelete;
        BackdropSlider.Value = S.BackdropIntensity;
        TopChk.IsChecked = S.AlwaysOnTop;
        TrayChk.IsChecked = S.MinimizeToTray;
        StartMinChk.IsChecked = S.StartMinimized;
        StartupChk.IsChecked = S.RunAtStartup || StartupShortcut.IsEnabled();

        // ── updates ──
        AutoCheckChk.IsChecked = S.AutoCheckUpdates;
        AutoInstallChk.IsChecked = S.AutoInstallUpdates;
        IntervalSlider.Value = S.UpdateCheckHours;
        BuildLabel.Text = UpdateService.BuildDescription;
        RepoLabel.Text = "Watching " + UpdateService.RepoUrl + " (branch " + UpdateService.Branch + ")";
        RefreshUpdateStatus();

        // ── storage ──
        ClipsPathLabel.Text = S.ClipsFolder;
        VersionLabel.Text = "Version " +
            (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

        UpdateAllLabels();
        UpdateFolderSizes();
    }

    private static void FillDevices(ComboBox box, List<AudioDeviceInfo> devices, string selectedId)
    {
        box.Items.Clear();
        foreach (var d in devices) box.Items.Add(d);
        var idx = devices.FindIndex(d => d.Id == selectedId);
        box.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private static string SelectedId(ComboBox box)
        => (box.SelectedItem as AudioDeviceInfo)?.Id ?? "";

    // ══════════════════════════════════════════════════════════
    //  EVENT WIRING
    // ══════════════════════════════════════════════════════════
    private void Wire()
    {
        LoopbackBox.SelectionChanged += (s, e) =>
        {
            if (_loading) return;
            S.LoopbackDeviceId = SelectedId(LoopbackBox);
            RestartCaptureIfRunning();
        };
        InputBox.SelectionChanged += (s, e) =>
        {
            if (_loading) return;
            S.InputDeviceId = SelectedId(InputBox);
            RestartCaptureIfRunning();
        };
        AutoStartChk.Click += (s, e) => S.AutoStartCapture = AutoStartChk.IsChecked == true;

        QuickBox.LostFocus += (s, e) => ParseQuickLengths();
        TemplateBox.LostFocus += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(TemplateBox.Text)) S.NameTemplate = TemplateBox.Text.Trim();
        };
        NormaliseChk.Click += (s, e) => S.NormalizeOnClip = NormaliseChk.IsChecked == true;
        TrimChk.Click += (s, e) => S.TrimSilence = TrimChk.IsChecked == true;
        OpenEditorChk.Click += (s, e) => S.OpenEditorAfterClip = OpenEditorChk.IsChecked == true;
        DefaultCatBox.SelectionChanged += (s, e) =>
        {
            if (_loading) return;
            S.DefaultCategoryId = (DefaultCatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        };

        OutputBox.SelectionChanged += (s, e) =>
        {
            if (_loading) return;
            S.OutputDeviceId = SelectedId(OutputBox);
            Core.Playback.Configure(S);
            Live();
            UpdateStatusLines();
        };
        MonitorBox.SelectionChanged += (s, e) =>
        {
            if (_loading) return;
            S.MonitorDeviceId = SelectedId(MonitorBox);
            Core.Playback.Configure(S);
            Live();
            UpdateStatusLines();
        };
        OverlapChk.Click += (s, e) => { S.AllowOverlap = OverlapChk.IsChecked == true; Core.Playback.Configure(S); };
        StopPrevChk.Click += (s, e) => { S.StopPreviousOnPlay = StopPrevChk.IsChecked == true; Core.Playback.Configure(S); };
        RetriggerChk.Click += (s, e) => { S.RestartOnRetrigger = RetriggerChk.IsChecked == true; Core.Playback.Configure(S); };

        FormatBox.SelectionChanged += (s, e) =>
        {
            if (_loading) return;
            S.DefaultExportFormat = (ExportFormat)Math.Max(0, FormatBox.SelectedIndex);
        };
        BitrateBox.SelectionChanged += (s, e) =>
        {
            if (_loading) return;
            S.Mp3Bitrate = BitrateBox.SelectedIndex switch
            {
                0 => 96, 1 => 128, 2 => 160, 3 => 192, 4 => 256, 5 => 320, _ => 192
            };
        };
        RevealChk.Click += (s, e) => S.OpenFolderAfterExport = RevealChk.IsChecked == true;

        PadSizeBox.SelectionChanged += (s, e) =>
        {
            if (_loading) return;
            S.PadSize = (PadSize)Math.Max(0, PadSizeBox.SelectedIndex);
            Live();
        };
        WaveChk.Click += (s, e) => { S.ShowWaveformOnPads = WaveChk.IsChecked == true; Live(); };
        AnimChk.Click += (s, e) => S.Animations = AnimChk.IsChecked == true;
        ToastChk.Click += (s, e) => S.ShowToastOnClip = ToastChk.IsChecked == true;
        ConfirmChk.Click += (s, e) => S.ConfirmDelete = ConfirmChk.IsChecked == true;
        TopChk.Click += (s, e) => { S.AlwaysOnTop = TopChk.IsChecked == true; Live(); };
        TrayChk.Click += (s, e) => S.MinimizeToTray = TrayChk.IsChecked == true;
        StartMinChk.Click += (s, e) => S.StartMinimized = StartMinChk.IsChecked == true;
        StartupChk.Click += (s, e) =>
        {
            S.RunAtStartup = StartupChk.IsChecked == true;
            StartupShortcut.Apply(S.RunAtStartup);
        };

        AutoCheckChk.Click += (s, e) =>
        {
            S.AutoCheckUpdates = AutoCheckChk.IsChecked == true;
            IntervalSlider.IsEnabled = S.AutoCheckUpdates;
            AutoInstallChk.IsEnabled = S.AutoCheckUpdates;
        };
        AutoInstallChk.Click += (s, e) => S.AutoInstallUpdates = AutoInstallChk.IsChecked == true;
    }

    private void ParseQuickLengths()
    {
        var parts = QuickBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var vals = new List<double>();
        foreach (var p in parts)
            if (double.TryParse(p.Trim(), out var v) && v >= 0.5 && v <= 900)
                vals.Add(Math.Round(v, 1));

        if (vals.Count > 0)
        {
            S.QuickLengths = vals.Take(6).ToList();
            Live();
        }
        QuickBox.Text = string.Join(", ", S.QuickLengths.Select(v => v.ToString("0.#")));
    }

    private void Live() => Main?.ApplySettings(startCapture: false);

    private void RestartCaptureIfRunning()
    {
        if (Core.Capture.IsCapturing) Core.Capture.Start(S);
        UpdateStatusLines();
    }

    // ══════════════════════════════════════════════════════════
    //  HOTKEY ROWS
    // ══════════════════════════════════════════════════════════
    private void BuildHotkeyRows()
    {
        HotkeyRows.Children.Clear();
        for (int i = 0; i < Actions.Length; i++)
        {
            var (action, label, hint) = Actions[i];

            if (i > 0)
                HotkeyRows.Children.Add(new Border
                {
                    Height = 1,
                    Background = (System.Windows.Media.Brush)FindResource("StrokeSoft"),
                    Margin = new Thickness(0, 11, 0, 11)
                });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });

            var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            text.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("RowTitle") });
            text.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("RowHint") });
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            S.Hotkeys.TryGetValue(action, out var gesture);
            var btn = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Height = 34,
                VerticalAlignment = VerticalAlignment.Top,
                Content = string.IsNullOrWhiteSpace(gesture) ? "Not set" : HotkeyService.Pretty(gesture),
                Tag = action
            };
            btn.Click += (s, e) => RebindHotkey((Button)s, action, label);
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            HotkeyRows.Children.Add(grid);
        }
    }

    private void RebindHotkey(Button btn, string action, string label)
    {
        S.Hotkeys.TryGetValue(action, out var current);
        var gesture = HotkeyDialog.Capture(this, label, current);
        if (gesture == null) return;

        if (!string.IsNullOrEmpty(gesture))
        {
            var clash = S.Hotkeys.FirstOrDefault(kv => kv.Key != action && kv.Value == gesture);
            if (clash.Key != null) S.Hotkeys[clash.Key] = "";

            var clipClash = Core.Library.Clips.FirstOrDefault(c => c.Hotkey == gesture);
            if (clipClash != null) clipClash.Hotkey = "";
        }

        S.Hotkeys[action] = gesture;
        SettingsService.Save(S);
        Core.Library.SaveSoon();

        BuildHotkeyRows();
        Main?.RegisterHotkeys();
        Live();
    }

    private void HotkeysEnabled_Changed(object sender, RoutedEventArgs e)
    {
        S.HotkeysEnabled = HotkeysChk.IsChecked == true;
        Main?.RegisterHotkeys();
    }

    // ══════════════════════════════════════════════════════════
    //  SLIDER / COMBO HANDLERS
    // ══════════════════════════════════════════════════════════
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.CaptureSource = ModeBox.SelectedIndex == 0 ? CaptureSource.Loopback : CaptureSource.InputDevice;
        LoopbackRow.Visibility = S.CaptureSource == CaptureSource.Loopback ? Visibility.Visible : Visibility.Collapsed;
        InputRow.Visibility = S.CaptureSource == CaptureSource.Loopback ? Visibility.Collapsed : Visibility.Visible;
        RestartCaptureIfRunning();
    }

    private void Buffer_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        var secs = (int)BufferSlider.Value;
        BufferLabel.Text = secs >= 60 ? $"{secs / 60}m {secs % 60:00}s  (~{secs * 0.183:0.#} MB)"
                                      : $"{secs}s  (~{secs * 0.183:0.#} MB)";
        if (_loading) return;
        S.BufferSeconds = secs;
        if (Core.Capture.IsCapturing) Core.Capture.ResizeBuffer(secs);
    }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        GainLabel.Text = $"{GainSlider.Value * 100:0}%";
        if (_loading) return;
        S.CaptureGain = GainSlider.Value;
        Core.Capture.CaptureGain = (float)GainSlider.Value;
    }

    private void ClipLen_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        var v = Math.Round(ClipLenSlider.Value);
        ClipLenLabel.Text = v >= 60 ? $"{v / 60:0.#} min" : $"{v:0} seconds";
        if (_loading) return;
        S.DefaultClipSeconds = v;
        Live();
    }

    private void Offset_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        OffsetLabel.Text = $"{OffsetSlider.Value:0} ms";
        if (_loading) return;
        S.ReactionOffsetMs = (int)OffsetSlider.Value;
    }

    private void Norm_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        NormLabel.Text = $"{NormSlider.Value:0.0} dBFS";
        if (_loading) return;
        S.NormalizeTargetDb = NormSlider.Value;
    }

    private void Silence_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        SilenceLabel.Text = $"{SilenceSlider.Value:0} dB";
        if (_loading) return;
        S.SilenceThresholdDb = SilenceSlider.Value;
    }

    private void Fades_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        FadeInLabel.Text = $"{FadeInSlider.Value:0} ms";
        FadeOutLabel.Text = $"{FadeOutSlider.Value:0} ms";
        if (_loading) return;
        S.FadeInMs = (int)FadeInSlider.Value;
        S.FadeOutMs = (int)FadeOutSlider.Value;
    }

    private void Volumes_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        MasterLabel.Text = $"{MasterSlider.Value * 100:0}%";
        MonitorLabel.Text = $"{MonitorSlider.Value * 100:0}%";
        if (_loading) return;
        S.MasterVolume = MasterSlider.Value;
        S.MonitorVolume = MonitorSlider.Value;
        Core.Playback.SetVolumes(S.MasterVolume, S.MonitorVolume);
        Live();
    }

    private void Latency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        LatencyLabel.Text = $"{LatencySlider.Value:0} ms";
        if (_loading) return;
        S.PlaybackLatencyMs = (int)LatencySlider.Value;
    }

    private void Ogg_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        OggLabel.Text = $"q{OggSlider.Value * 10:0.#}";
        if (_loading) return;
        S.OggQuality = (float)OggSlider.Value;
    }

    private void Backdrop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        BackdropLabel.Text = BackdropSlider.Value <= 0.01 ? "off" : $"{BackdropSlider.Value * 100:0}%";
        if (_loading) return;
        S.BackdropIntensity = BackdropSlider.Value;
        Live();
    }

    private void Monitor_Changed(object sender, RoutedEventArgs e)
    {
        S.MonitorEnabled = MonitorChk.IsChecked == true;
        MonitorBox.IsEnabled = S.MonitorEnabled;
        Core.Playback.Configure(S);
        Live();
        UpdateStatusLines();
    }

    private void UpdateAllLabels()
    {
        Buffer_Changed(null, null);
        Gain_Changed(null, null);
        ClipLen_Changed(null, null);
        Offset_Changed(null, null);
        Norm_Changed(null, null);
        Silence_Changed(null, null);
        Fades_Changed(null, null);
        Volumes_Changed(null, null);
        Latency_Changed(null, null);
        Ogg_Changed(null, null);
        Backdrop_Changed(null, null);
        Interval_Changed(null, null);
    }

    private void UpdateStatusLines()
    {
        CaptureStatus.Text = Core.Capture.IsCapturing
            ? $"Running on “{Core.Capture.DeviceName}” · {Core.Capture.SampleRate / 1000.0:0.#} kHz · "
              + $"{Core.Capture.Channels} ch · {Core.Capture.BufferFill:0}s buffered"
            : "Capture is stopped — nothing can be clipped right now.";

        OutputStatus.Text = $"Main output: {Core.Playback.PrimaryStatus}   ·   Monitor: {Core.Playback.MonitorStatus}";
    }

    private void UpdateFolderSizes()
    {
        try
        {
            long bytes = 0; int files = 0;
            if (Directory.Exists(S.ClipsFolder))
                foreach (var f in Directory.EnumerateFiles(S.ClipsFolder, "*.wav"))
                { bytes += new FileInfo(f).Length; files++; }
            ClipsSizeLabel.Text = $"{files} file(s) · {bytes / 1024.0 / 1024.0:0.#} MB";
        }
        catch { ClipsSizeLabel.Text = ""; }
    }

    // ══════════════════════════════════════════════════════════
    //  BUTTONS
    // ══════════════════════════════════════════════════════════
    private void RestartCapture_Click(object sender, RoutedEventArgs e)
    {
        Core.Capture.Start(S);
        UpdateStatusLines();
    }

    private void StopCapture_Click(object sender, RoutedEventArgs e)
    {
        Core.Capture.Stop();
        UpdateStatusLines();
    }

    private void ClearBuffer_Click(object sender, RoutedEventArgs e)
    {
        Core.Capture.ClearBuffer();
        UpdateStatusLines();
    }

    private void ChangeExportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose export folder",
            InitialDirectory = Directory.Exists(S.ExportFolder) ? S.ExportFolder : ""
        };
        if (dlg.ShowDialog(this) != true) return;
        S.ExportFolder = dlg.FolderName;
        ExportPathLabel.Text = S.ExportFolder;
    }

    private void OpenExportFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(S.ExportFolder);
    private void OpenClipsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(S.ClipsFolder);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start("explorer.exe", "\"" + path + "\"");
        }
        catch { }
    }

    private void ChangeClipsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose clips folder",
            InitialDirectory = Directory.Exists(S.ClipsFolder) ? S.ClipsFolder : ""
        };
        if (dlg.ShowDialog(this) != true) return;

        var target = dlg.FolderName;
        if (string.Equals(target, S.ClipsFolder, StringComparison.OrdinalIgnoreCase)) return;

        var move = Prompt.Confirm(this, "Move existing clips?",
            $"Copy the {Core.Library.Clips.Count} clip file(s) into the new folder as well? " +
            "Choose Cancel to just point VoidClip at the new location.",
            "Copy files");

        try
        {
            Directory.CreateDirectory(target);
            if (move)
            {
                foreach (var clip in Core.Library.Clips)
                {
                    var src = Path.Combine(S.ClipsFolder, clip.FileName);
                    var dst = Path.Combine(target, clip.FileName);
                    if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
                }
            }
            S.ClipsFolder = target;
            Core.Library.ClipsFolder = target;
            ClipsPathLabel.Text = target;
            UpdateFolderSizes();
            SettingsService.Save(S);
            Live();
        }
        catch (Exception ex)
        {
            Prompt.Info(this, "Could not switch folder", ex.Message);
        }
    }

    private void Prune_Click(object sender, RoutedEventArgs e)
    {
        var missing = Core.Library.Clips
            .Where(c => !File.Exists(Core.Library.PathOf(c)))
            .ToList();

        if (missing.Count == 0)
        {
            Prompt.Info(this, "Nothing to clean", "Every clip in the library still has its audio file.");
            return;
        }

        if (!Prompt.Confirm(this, "Remove missing clips",
            $"{missing.Count} clip(s) point at files that no longer exist. Remove them from the library?",
            "Remove", danger: true)) return;

        foreach (var c in missing) Core.Library.Delete(c, deleteFile: false);
        Live();
        UpdateFolderSizes();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(AppPaths.LogFile)) File.WriteAllText(AppPaths.LogFile, "");
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════
    //  UPDATES
    // ══════════════════════════════════════════════════════════
    private void Interval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_built) return;
        var hours = (int)IntervalSlider.Value;
        IntervalLabel.Text = hours == 1 ? "every hour"
            : hours < 24 ? $"every {hours} hours"
            : hours == 24 ? "once a day"
            : $"every {hours / 24.0:0.#} days";
        if (_loading) return;
        S.UpdateCheckHours = hours;
    }

    private void RefreshUpdateStatus()
    {
        IntervalSlider.IsEnabled = S.AutoCheckUpdates;
        AutoInstallChk.IsEnabled = S.AutoCheckUpdates;

        UnskipBtn.Visibility = string.IsNullOrEmpty(S.SkippedUpdateTag)
            ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrEmpty(S.PendingUpdateVersion))
        {
            UpdateStatusLabel.Text = $"{S.PendingUpdateVersion} is installed and waiting for a restart.";
            UpdateStatusDetail.Text = "It applies the next time you open VoidClip.";
            return;
        }

        UpdateStatusLabel.Text = S.LastUpdateCheck.HasValue
            ? "Last checked " + Friendly(S.LastUpdateCheck.Value)
            : "Not checked yet";

        UpdateStatusDetail.Text = string.IsNullOrEmpty(S.SkippedUpdateTag)
            ? ""
            : $"Skipping release “{S.SkippedUpdateTag}” — you dismissed it.";
    }

    private static string Friendly(DateTime when)
    {
        var ago = DateTime.Now - when;
        if (ago.TotalMinutes < 1) return "just now";
        if (ago.TotalMinutes < 60) return $"{ago.TotalMinutes:0} min ago";
        if (ago.TotalHours < 24) return $"{ago.TotalHours:0} hours ago";
        return when.ToString("dd MMM yyyy HH:mm");
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowBtn.IsEnabled = false;
        CheckNowBtn.Content = "Checking…";
        UpdateStatusDetail.Text = "";
        try
        {
            var check = Main != null
                ? await Main.CheckForUpdates(interactive: true)
                : await UpdateService.CheckAsync();

            if (check == null) { UpdateStatusLabel.Text = "A check is already running."; return; }

            UpdateStatusLabel.Text = check.Summary;

            var bits = new List<string>();
            if (!string.IsNullOrEmpty(check.LatestCommitSha))
                bits.Add("main is at " + check.LatestCommitSha[..Math.Min(8, check.LatestCommitSha.Length)]);
            if (check.Release != null)
                bits.Add("latest release: " + check.Release.DisplayName);
            UpdateStatusDetail.Text = string.Join("  ·  ", bits);
        }
        finally
        {
            CheckNowBtn.IsEnabled = true;
            CheckNowBtn.Content = "Check now";
            UnskipBtn.Visibility = string.IsNullOrEmpty(S.SkippedUpdateTag)
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void Unskip_Click(object sender, RoutedEventArgs e)
    {
        S.SkippedUpdateTag = "";
        SettingsService.Save(S);
        RefreshUpdateStatus();
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
        => UpdateService.OpenInBrowser(UpdateService.RepoUrl);

    private void OpenReleases_Click(object sender, RoutedEventArgs e)
        => UpdateService.OpenInBrowser(UpdateService.ReleasesUrl);

    private void OpenCommits_Click(object sender, RoutedEventArgs e)
        => UpdateService.OpenInBrowser(UpdateService.CommitsUrl);

    private void SelfTest_Click(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        string report;
        try { report = SelfTest.Run(); }
        finally { Mouse.OverrideCursor = null; }

        var failed = report.Contains("FAIL");
        Prompt.Info(this, failed ? "Self-test found problems" : "Self-test passed", report);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!Prompt.Confirm(this, "Reset settings",
            "Every setting goes back to its default. Your clips and categories are not touched.",
            "Reset", danger: true)) return;

        var clips = Core.Settings.ClipsFolder;
        Core.Settings = new AppSettings { ClipsFolder = clips };
        SettingsService.Save(Core.Settings);

        _loading = true;
        Populate();
        BuildHotkeyRows();
        _loading = false;

        Main?.RegisterHotkeys();
        Live();
        UpdateStatusLines();
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        ParseQuickLengths();
        if (!string.IsNullOrWhiteSpace(TemplateBox.Text)) S.NameTemplate = TemplateBox.Text.Trim();
        SettingsService.Save(S);
        base.OnClosed(e);
    }
}
