using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VoidClip.Audio;
using VoidClip.Models;
using VoidClip.Services;
using VoidClip.Views;

namespace VoidClip;

public partial class MainWindow : Window
{
    private enum Filter { All, Favourites, Uncategorised, Category }

    private Filter _filter = Filter.All;
    private string _filterCategoryId = "";
    private string _search = "";
    private readonly ObservableCollection<Clip> _view = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private System.Windows.Forms.NotifyIcon _tray;
    private float _level;
    private float _levelShown;
    private Clip _lastClip;
    private bool _ready;
    private bool _swallowNextPadUp;

    private AppSettings S => Core.Settings;

    // ── pad metrics, bound from the pad template ──
    public static readonly DependencyProperty PadWidthProperty =
        DependencyProperty.Register(nameof(PadWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(176.0));
    public static readonly DependencyProperty PadHeightProperty =
        DependencyProperty.Register(nameof(PadHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(104.0));
    public static readonly DependencyProperty ShowPadWavesProperty =
        DependencyProperty.Register(nameof(ShowPadWaves), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    public double PadWidth { get => (double)GetValue(PadWidthProperty); set => SetValue(PadWidthProperty, value); }
    public double PadHeight { get => (double)GetValue(PadHeightProperty); set => SetValue(PadHeightProperty, value); }
    public bool ShowPadWaves { get => (bool)GetValue(ShowPadWavesProperty); set => SetValue(ShowPadWavesProperty, value); }

    // ══════════════════════════════════════════════════════════
    public MainWindow()
    {
        InitializeComponent();
        Board.ItemsSource = _view;
        CategoryList.ItemsSource = Core.Library.Categories;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();

        Core.Capture.LevelChanged += p => _level = p;
        Core.Capture.StatusChanged += (running, msg) => Dispatcher.BeginInvoke(() => UpdateCaptureUi(running, msg));
        Core.Playback.ClipStarted += id => Dispatcher.BeginInvoke(() => SetPlaying(id, true));
        Core.Playback.ClipStopped += id => Dispatcher.BeginInvoke(() => SetPlaying(id, false));
        Core.Playback.Problem += msg => Dispatcher.BeginInvoke(() => ShowToast("Playback problem", msg, true));

        Core.Hotkeys.Attach(this);
        Core.Hotkeys.Pressed += OnHotkey;
        Core.Hotkeys.RegistrationFailed += (action, gesture) => Dispatcher.BeginInvoke(() =>
            ShowToast("Hotkey unavailable", $"{HotkeyService.Pretty(gesture)} is taken by another app.", true));

        TitleHint.ToolTip = "Ctrl+,  settings      Ctrl+F  search      Ctrl+N  new category      Ctrl+R  pause capture";

        SetupTray();
        ApplySettings(startCapture: true);

        PreviewKeyDown += OnKeyDown;

        _tick.Tick += OnTick;
        _tick.Start();

        _ready = true;
        RefreshBoard();

        if (S.StartMinimized) { WindowState = WindowState.Minimized; }
    }

    // ══════════════════════════════════════════════════════════
    //  SETTINGS APPLICATION
    // ══════════════════════════════════════════════════════════
    public void ApplySettings(bool startCapture)
    {
        (PadWidth, PadHeight) = S.PadSize switch
        {
            PadSize.Small => (146.0, 88.0),
            PadSize.Large => (218.0, 128.0),
            _ => (176.0, 104.0)
        };
        ShowPadWaves = S.ShowWaveformOnPads;

        Topmost = S.AlwaysOnTop;
        BloomA.Opacity = 0.85 * S.BackdropIntensity;
        BloomB.Opacity = 0.70 * S.BackdropIntensity;
        BloomC.Opacity = 0.55 * S.BackdropIntensity;

        MasterVol.Value = S.MasterVolume;
        MonitorVol.Value = S.MonitorVolume;
        MonitorChk.IsChecked = S.MonitorEnabled;
        MonitorVol.IsEnabled = S.MonitorEnabled;
        SortBox.SelectedIndex = (int)S.SortMode;

        Core.Library.ClipsFolder = S.ClipsFolder;
        Core.Capture.CaptureGain = (float)S.CaptureGain;

        BuildQuickButtons();
        UpdateClipButton();
        Core.Playback.Configure(S);
        UpdateOutputLabel();
        RegisterHotkeys();
        StartupShortcut.Apply(S.RunAtStartup);

        if (startCapture && S.AutoStartCapture && !Core.Capture.IsCapturing)
            Core.Capture.Start(S);
        else if (Core.Capture.IsCapturing && Core.Capture.BufferCapacitySeconds != S.BufferSeconds)
            Core.Capture.ResizeBuffer(S.BufferSeconds);

        UpdateStorageLabel();
        RefreshBoard();
    }

    private void BuildQuickButtons()
    {
        QuickPanel.Children.Clear();
        foreach (var len in S.QuickLengths.Take(6))
        {
            var b = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Content = len >= 60 ? $"{len / 60:0.#}m" : $"{len:0.#}s",
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(13, 9, 13, 9),
                ToolTip = $"Clip the last {len:0.#} seconds",
                Tag = len
            };
            b.Click += (s, e) => DoClip((double)((Button)s).Tag);
            QuickPanel.Children.Add(b);
        }
    }

    private void UpdateClipButton()
    {
        var secs = S.DefaultClipSeconds;
        ClipBtnTitle.Text = secs >= 60 ? $"CLIP LAST {secs / 60:0.#}m" : $"CLIP LAST {secs:0.#}s";
        ClipBtnHint.Text = S.Hotkeys.TryGetValue("ClipNow", out var g) && !string.IsNullOrWhiteSpace(g)
            ? HotkeyService.Pretty(g)
            : "no hotkey set";
    }

    private void UpdateOutputLabel()
    {
        var main = AudioDevices.NameOf(S.OutputDeviceId, NAudio.CoreAudioApi.DataFlow.Render);
        var mon = S.MonitorEnabled
            ? " · monitor: " + AudioDevices.NameOf(S.MonitorDeviceId, NAudio.CoreAudioApi.DataFlow.Render)
            : "";
        OutputLabel.Text = "→ " + main + mon;
        OutputLabel.ToolTip = OutputLabel.Text;
    }

    private void UpdateStorageLabel()
    {
        try
        {
            long bytes = 0;
            var dir = Core.Library.ClipsFolder;
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.wav"))
                    bytes += new FileInfo(f).Length;
            var mb = bytes / 1024.0 / 1024.0;
            StorageLabel.Text = $"{Core.Library.Clips.Count} clips · {mb:0.#} MB on disk";
        }
        catch { StorageLabel.Text = $"{Core.Library.Clips.Count} clips"; }
    }

    // ══════════════════════════════════════════════════════════
    //  HOTKEYS
    // ══════════════════════════════════════════════════════════
    public void RegisterHotkeys()
    {
        Core.Hotkeys.UnregisterAll();
        Core.Hotkeys.Enabled = S.HotkeysEnabled;
        if (!S.HotkeysEnabled) return;

        foreach (var kv in S.Hotkeys)
            if (!string.IsNullOrWhiteSpace(kv.Value))
                Core.Hotkeys.Register(kv.Key, kv.Value);

        foreach (var clip in Core.Library.Clips)
            if (!string.IsNullOrWhiteSpace(clip.Hotkey))
                Core.Hotkeys.Register("clip:" + clip.Id, clip.Hotkey);
    }

    private void OnHotkey(string action)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (action.StartsWith("clip:"))
            {
                var id = action[5..];
                var clip = Core.Library.Clips.FirstOrDefault(c => c.Id == id);
                if (clip != null) Fire(clip);
                return;
            }

            switch (action)
            {
                case "ClipNow": DoClip(S.DefaultClipSeconds); break;
                case "StopAll": Core.Playback.StopAll(); break;
                case "ToggleCapture": ToggleCapture(); break;
                case "ShowHide": ToggleWindow(); break;
                case "ReplayLast":
                    var last = _lastClip ?? Core.Library.Clips.FirstOrDefault();
                    if (last != null) Fire(last);
                    break;
                default:
                    if (action.StartsWith("ClipQuick") && int.TryParse(action[9..], out var n)
                        && n >= 1 && n <= S.QuickLengths.Count)
                        DoClip(S.QuickLengths[n - 1]);
                    break;
            }
        });
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key == Key.OemComma) { Settings_Click(null, null); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.N) { AddCategory(); e.Handled = true; }
        else if (ctrl && e.Key == Key.R) { ToggleCapture(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text)) { SearchBox.Clear(); Keyboard.ClearFocus(); }
            e.Handled = true;
        }
    }

    private void ToggleCapture()
    {
        if (Core.Capture.IsCapturing) { Core.Capture.Stop(); ShowToast("Capture paused", "Nothing is being buffered."); }
        else { Core.Capture.Start(S); ShowToast("Capture running", "Buffering " + S.BufferSeconds + "s of history."); }
    }

    private void ToggleWindow()
    {
        if (IsVisible && WindowState != WindowState.Minimized) { WindowState = WindowState.Minimized; }
        else RestoreWindow();
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = S.AlwaysOnTop;
    }

    // ══════════════════════════════════════════════════════════
    //  CLIPPING
    // ══════════════════════════════════════════════════════════
    public void DoClip(double seconds)
    {
        if (!Core.Capture.IsCapturing)
        {
            ShowToast("Not capturing", "Start capture first (Ctrl+Alt+R or Settings ▸ Capture).", true);
            return;
        }

        var samples = Core.Capture.Grab(seconds, S.ReactionOffsetMs / 1000.0);
        if (samples.Length == 0)
        {
            ShowToast("Buffer empty", "Nothing has come through the capture device yet.", true);
            return;
        }

        if (S.TrimSilence)
            samples = Dsp.TrimSilence(samples, Fmt.Channels, Fmt.Rate, S.SilenceThresholdDb);
        if (S.NormalizeOnClip)
            Dsp.Normalize(samples, S.NormalizeTargetDb);
        Dsp.Fade(samples, Fmt.Channels, S.FadeInMs, S.FadeOutMs, Fmt.Rate);

        var categoryId = _filter == Filter.Category ? _filterCategoryId : S.DefaultCategoryId;
        var name = Core.Library.NextClipName(S.NameTemplate);
        var clip = Core.Library.AddClip(samples, name, categoryId);
        _lastClip = clip;

        RefreshBoard();
        UpdateStorageLabel();
        ShowToast("Clipped", $"{clip.Name} · {clip.DurationText}");

        if (S.OpenEditorAfterClip) EditClip(clip);
    }

    // ══════════════════════════════════════════════════════════
    //  BOARD
    // ══════════════════════════════════════════════════════════
    public void RefreshBoard()
    {
        if (!_ready) return;

        IEnumerable<Clip> q = Core.Library.Clips;

        q = _filter switch
        {
            Filter.Favourites => q.Where(c => c.Favorite),
            Filter.Uncategorised => q.Where(c => string.IsNullOrEmpty(c.CategoryId)
                                                 || Core.Library.CategoryById(c.CategoryId) == null),
            Filter.Category => q.Where(c => c.CategoryId == _filterCategoryId),
            _ => q
        };

        if (!string.IsNullOrWhiteSpace(_search))
            q = q.Where(c => c.Name.Contains(_search, StringComparison.OrdinalIgnoreCase));

        q = S.SortMode switch
        {
            SortMode.OldestFirst => q.OrderBy(c => c.Created),
            SortMode.NameAsc => q.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
            SortMode.NameDesc => q.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase),
            SortMode.MostPlayed => q.OrderByDescending(c => c.PlayCount).ThenByDescending(c => c.Created),
            SortMode.LongestFirst => q.OrderByDescending(c => c.Duration),
            _ => q.OrderByDescending(c => c.Created)
        };

        _view.Clear();
        foreach (var c in q) _view.Add(c);

        Core.Library.RecountCategories();
        AllCount.Text = Core.Library.Clips.Count.ToString();
        FavCount.Text = Core.Library.Clips.Count(c => c.Favorite).ToString();
        NoneCount.Text = Core.Library.Clips.Count(c => string.IsNullOrEmpty(c.CategoryId)
                                                       || Core.Library.CategoryById(c.CategoryId) == null).ToString();

        foreach (var cat in Core.Library.Categories)
            cat.IsSelected = _filter == Filter.Category && cat.Id == _filterCategoryId;

        var selected = (Brush)FindResource("Void3");
        FilterAll.Background = _filter == Filter.All ? selected : Brushes.Transparent;
        FilterFav.Background = _filter == Filter.Favourites ? selected : Brushes.Transparent;
        FilterNone.Background = _filter == Filter.Uncategorised ? selected : Brushes.Transparent;

        var empty = _view.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BoardScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            var searching = !string.IsNullOrWhiteSpace(_search);
            var anyClips = Core.Library.Clips.Count > 0;
            EmptyTitle.Text = searching ? "No matches" : anyClips ? "Nothing here" : "Nothing clipped yet";
            EmptyBody.Text = searching
                ? $"No clip name contains “{_search}”."
                : anyClips
                    ? "This filter has no clips in it. Pick another category, or clip something new."
                    : "VoidClip is holding the last few minutes of your speakers in memory. When someone says something worth keeping, hit the clip hotkey — it lands here as a pad you can fire straight back.";
        }
    }

    private void SetPlaying(string clipId, bool playing)
    {
        var clip = Core.Library.Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip != null) clip.IsPlaying = playing;

        var n = Core.Playback.ActiveCount;
        PlayingLabel.Text = n == 0 ? "" : n == 1 ? "1 playing" : $"{n} playing";
    }

    private void Fire(Clip clip)
    {
        if (clip.IsPlaying && !S.RestartOnRetrigger)
        {
            Core.Playback.StopClip(clip.Id);
            return;
        }
        var samples = Core.Library.GetSamples(clip);
        if (samples.Length == 0)
        {
            ShowToast("Missing audio", $"The file for “{clip.Name}” could not be read.", true);
            return;
        }
        Core.Playback.Play(clip.Id, samples, (float)clip.Volume, clip.Loop);
        clip.PlayCount++;
        _lastClip = clip;
        Core.Library.SaveSoon();
    }

    // ══════════════════════════════════════════════════════════
    //  PAD INTERACTION
    // ══════════════════════════════════════════════════════════
    /// <summary>
    /// Double-click is only reliable on the down event — Windows sends the second press
    /// as WM_LBUTTONDBLCLK, so the matching up event no longer carries a count of 2.
    /// </summary>
    private void Pad_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (((FrameworkElement)sender).DataContext is not Clip clip) return;

        _swallowNextPadUp = true;
        Core.Playback.StopClip(clip.Id);
        EditClip(clip);
        e.Handled = true;
    }

    private void Pad_Click(object sender, MouseButtonEventArgs e)
    {
        if (_swallowNextPadUp) { _swallowNextPadUp = false; return; }
        if (((FrameworkElement)sender).DataContext is not Clip clip) return;
        Fire(clip);
    }

    private void Pad_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Clip clip) return;
        var menu = BuildClipMenu(clip);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildClipMenu(Clip clip)
    {
        var menu = new ContextMenu();

        menu.Items.Add(Item(clip.IsPlaying ? "Stop" : "Play", () =>
        {
            if (clip.IsPlaying) Core.Playback.StopClip(clip.Id); else Fire(clip);
        }));
        menu.Items.Add(Item("Edit & trim…", () => EditClip(clip)));
        menu.Items.Add(Item("Rename…", () => RenameClip(clip)));
        menu.Items.Add(new Separator());

        // ── categories ──
        var cats = new MenuItem { Header = "Move to category" };
        var none = Item("Uncategorised", () => { clip.CategoryId = ""; Core.Library.SaveSoon(); RefreshBoard(); });
        none.IsChecked = string.IsNullOrEmpty(clip.CategoryId);
        cats.Items.Add(none);
        if (Core.Library.Categories.Count > 0) cats.Items.Add(new Separator());
        foreach (var cat in Core.Library.Categories)
        {
            var c = cat;
            var mi = Item(c.Name, () => { clip.CategoryId = c.Id; Core.Library.SaveSoon(); RefreshBoard(); });
            mi.IsChecked = clip.CategoryId == c.Id;
            cats.Items.Add(mi);
        }
        cats.Items.Add(new Separator());
        cats.Items.Add(Item("New category…", () =>
        {
            var made = AddCategory();
            if (made != null) { clip.CategoryId = made.Id; Core.Library.SaveSoon(); RefreshBoard(); }
        }));
        menu.Items.Add(cats);

        // ── hotkey ──
        var hk = new MenuItem { Header = "Hotkey" };
        hk.Items.Add(Item(string.IsNullOrEmpty(clip.Hotkey) ? "Assign…" : "Change…", () => AssignHotkey(clip)));
        if (!string.IsNullOrEmpty(clip.Hotkey))
            hk.Items.Add(Item("Clear (" + HotkeyService.Pretty(clip.Hotkey) + ")", () =>
            {
                Core.Hotkeys.Unregister("clip:" + clip.Id);
                clip.Hotkey = "";
                Core.Library.SaveSoon();
                RefreshBoard();
            }));
        menu.Items.Add(hk);

        menu.Items.Add(Item(clip.Favorite ? "Remove from favourites" : "Add to favourites", () =>
        {
            clip.Favorite = !clip.Favorite;
            Core.Library.SaveSoon();
            RefreshBoard();
        }));
        menu.Items.Add(new Separator());

        // ── export ──
        var ex = new MenuItem { Header = "Export as" };
        ex.Items.Add(Item("MP3…", () => ExportClip(clip, ExportFormat.Mp3)));
        ex.Items.Add(Item("OGG…", () => ExportClip(clip, ExportFormat.Ogg)));
        ex.Items.Add(Item("WAV…", () => ExportClip(clip, ExportFormat.Wav)));
        menu.Items.Add(ex);

        menu.Items.Add(Item("Duplicate", () => DuplicateClip(clip)));
        menu.Items.Add(Item("Show file in Explorer", () =>
        {
            var p = Core.Library.PathOf(clip);
            if (File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", () => DeleteClip(clip)));

        return menu;
    }

    private static MenuItem Item(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (s, e) => action();
        return mi;
    }

    // ══════════════════════════════════════════════════════════
    //  CLIP ACTIONS
    // ══════════════════════════════════════════════════════════
    private void RenameClip(Clip clip)
    {
        var name = Prompt.Text(this, "Rename clip", "Give this one a name you'll recognise mid-conversation.", clip.Name);
        if (name == null) return;
        clip.Name = name;
        Core.Library.SaveSoon();
        RefreshBoard();
    }

    private void DeleteClip(Clip clip)
    {
        if (S.ConfirmDelete &&
            !Prompt.Confirm(this, "Delete clip",
                $"“{clip.Name}” and its audio file will be removed. This cannot be undone.",
                "Delete", danger: true))
            return;

        Core.Playback.StopClip(clip.Id);
        Core.Hotkeys.Unregister("clip:" + clip.Id);
        Core.Library.Delete(clip);
        if (_lastClip == clip) _lastClip = null;
        RefreshBoard();
        UpdateStorageLabel();
    }

    private void DuplicateClip(Clip clip)
    {
        var samples = Core.Library.GetSamples(clip);
        if (samples.Length == 0) return;
        var copy = Core.Library.AddClip((float[])samples.Clone(), clip.Name + " copy", clip.CategoryId);
        copy.Volume = clip.Volume;
        copy.Color = clip.Color;
        Core.Library.SaveSoon();
        RefreshBoard();
    }

    private void AssignHotkey(Clip clip)
    {
        var gesture = HotkeyDialog.Capture(this, "Hotkey for “" + clip.Name + "”", clip.Hotkey);
        if (gesture == null) return;

        var clash = Core.Library.Clips.FirstOrDefault(c => c != clip && c.Hotkey == gesture);
        if (clash != null) clash.Hotkey = "";
        var globalClash = S.Hotkeys.FirstOrDefault(kv => kv.Value == gesture);
        if (globalClash.Key != null)
        {
            ShowToast("Already in use", $"{HotkeyService.Pretty(gesture)} is bound to {globalClash.Key}.", true);
            return;
        }

        clip.Hotkey = gesture;
        Core.Library.SaveSoon();
        RegisterHotkeys();
        RefreshBoard();
    }

    private void ExportClip(Clip clip, ExportFormat format)
    {
        var samples = Core.Library.GetSamples(clip);
        if (samples.Length == 0) { ShowToast("Missing audio", "Nothing to export.", true); return; }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export clip",
            FileName = Sanitise(clip.Name) + Exporter.Extension(format),
            DefaultExt = Exporter.Extension(format),
            Filter = format switch
            {
                ExportFormat.Mp3 => "MP3 audio (*.mp3)|*.mp3",
                ExportFormat.Ogg => "Ogg Vorbis (*.ogg)|*.ogg",
                _ => "WAV audio (*.wav)|*.wav"
            },
            InitialDirectory = Directory.Exists(S.ExportFolder)
                ? S.ExportFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dlg.FileName)!);
            Exporter.Export(dlg.FileName, samples, format, S.Mp3Bitrate, S.OggQuality);
            S.ExportFolder = Path.GetDirectoryName(dlg.FileName);
            SettingsService.Save(S);
            ShowToast("Exported", Path.GetFileName(dlg.FileName));
            if (S.OpenFolderAfterExport)
                Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\"");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Export failed: " + ex);
            ShowToast("Export failed", ex.Message, true);
        }
    }

    private static string Sanitise(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "clip" : name.Trim();
    }

    private void EditClip(Clip clip)
    {
        var editor = new ClipEditor(clip) { Owner = this };
        var result = editor.ShowDialog();
        RegisterHotkeys();
        RefreshBoard();
        UpdateStorageLabel();
    }

    // ══════════════════════════════════════════════════════════
    //  CATEGORIES
    // ══════════════════════════════════════════════════════════
    private ClipCategory AddCategory()
    {
        var r = CategoryDialog.Show(this, "New category", "Create");
        if (r == null) return null;
        var cat = Core.Library.AddCategory(r.Value.name, r.Value.color);
        RefreshBoard();
        return cat;
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e) => AddCategory();

    private void Category_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ClipCategory cat) return;
        _filter = Filter.Category;
        _filterCategoryId = cat.Id;
        RefreshBoard();
    }

    private void Category_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ClipCategory cat) return;
        e.Handled = true;

        var menu = new ContextMenu();
        menu.Items.Add(Item("Edit…", () =>
        {
            var r = CategoryDialog.Show(this, "Edit category", "Save", cat.Name, cat.Color);
            if (r == null) return;
            cat.Name = r.Value.name;
            cat.Color = r.Value.color;
            Core.Library.SaveSoon();
            RefreshBoard();
        }));
        menu.Items.Add(Item("Set as default for new clips", () =>
        {
            S.DefaultCategoryId = cat.Id;
            SettingsService.Save(S);
            ShowToast("Default category", $"New clips land in “{cat.Name}”.");
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete (keep clips)", () =>
        {
            if (!Prompt.Confirm(this, "Delete category",
                $"“{cat.Name}” will be removed. Its {cat.Count} clip(s) become uncategorised.",
                "Delete", danger: true)) return;
            Core.Library.DeleteCategory(cat, deleteClips: false);
            if (_filterCategoryId == cat.Id) { _filter = Filter.All; _filterCategoryId = ""; }
            RefreshBoard();
        }));
        menu.Items.Add(Item("Delete with its clips", () =>
        {
            if (!Prompt.Confirm(this, "Delete category and clips",
                $"“{cat.Name}” and all {cat.Count} clip(s) inside it will be permanently deleted.",
                "Delete everything", danger: true)) return;
            foreach (var c in Core.Library.Clips.Where(c => c.CategoryId == cat.Id).ToList())
                Core.Hotkeys.Unregister("clip:" + c.Id);
            Core.Library.DeleteCategory(cat, deleteClips: true);
            if (_filterCategoryId == cat.Id) { _filter = Filter.All; _filterCategoryId = ""; }
            RefreshBoard();
            UpdateStorageLabel();
        }));

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void FilterAll_Click(object sender, MouseButtonEventArgs e) { _filter = Filter.All; RefreshBoard(); }
    private void FilterFav_Click(object sender, MouseButtonEventArgs e) { _filter = Filter.Favourites; RefreshBoard(); }
    private void FilterNone_Click(object sender, MouseButtonEventArgs e) { _filter = Filter.Uncategorised; RefreshBoard(); }

    // ══════════════════════════════════════════════════════════
    //  CHROME + MISC UI
    // ══════════════════════════════════════════════════════════
    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Max_Click(null, null); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow { Owner = this };
        w.ShowDialog();
        SettingsService.Save(S);
        ApplySettings(startCapture: false);
    }

    private void ClipNow_Click(object sender, RoutedEventArgs e) => DoClip(S.DefaultClipSeconds);

    private void StopAll_Click(object sender, RoutedEventArgs e) => Core.Playback.StopAll();

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        S.MasterVolume = MasterVol.Value;
        S.MonitorVolume = MonitorVol.Value;
        MasterVolLabel.Text = $"{MasterVol.Value * 100:0}%";
        Core.Playback.SetVolumes(S.MasterVolume, S.MonitorVolume);
    }

    private void Monitor_Changed(object sender, RoutedEventArgs e)
    {
        S.MonitorEnabled = MonitorChk.IsChecked == true;
        MonitorVol.IsEnabled = S.MonitorEnabled;
        Core.Playback.Configure(S);
        UpdateOutputLabel();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text;
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(_search) ? Visibility.Visible : Visibility.Collapsed;
        RefreshBoard();
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        S.SortMode = (SortMode)Math.Max(0, SortBox.SelectedIndex);
        RefreshBoard();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Core.Library.ClipsFolder);
            Process.Start("explorer.exe", "\"" + Core.Library.ClipsFolder + "\"");
        }
        catch (Exception ex) { ShowToast("Could not open folder", ex.Message, true); }
    }

    private void UpdateCaptureUi(bool running, string msg)
    {
        RecDot.Fill = running ? (Brush)FindResource("RecordGradient") : (Brush)FindResource("TextMute");
        RecDot.Effect = running ? (System.Windows.Media.Effects.Effect)FindResource("GlowRed") : null;
        CaptureLabel.Text = running
            ? (S.CaptureSource == CaptureSource.Loopback ? "Hearing " : "Recording ") + msg
            : msg;
        CaptureLabel.ToolTip = CaptureLabel.Text;
    }

    private void OnTick(object sender, EventArgs e)
    {
        // level meter with a smooth decay so it reads like a real meter
        _levelShown = Math.Max(_level, _levelShown - 0.06f);
        _level *= 0.55f;
        var w = LevelBar.Parent is FrameworkElement p ? p.ActualWidth : 0;
        LevelBar.Width = Math.Max(0, Math.Min(w, w * Math.Sqrt(_levelShown)));

        if (Core.Capture.IsCapturing)
        {
            var fill = Core.Capture.BufferFill;
            var cap = Core.Capture.BufferCapacitySeconds;
            BufferLabel.Text = fill >= cap - 0.5 ? $"{cap}s buffered" : $"{fill:0}s / {cap}s";
        }
        else BufferLabel.Text = "—";
    }

    // ══════════════════════════════════════════════════════════
    //  TOAST
    // ══════════════════════════════════════════════════════════
    public void ShowToast(string title, string body, bool warn = false)
    {
        if (!S.ShowToastOnClip && !warn) return;

        ToastTitle.Text = title;
        ToastBody.Text = body;
        Toast.BorderBrush = warn ? (Brush)FindResource("Red") : (Brush)FindResource("Purple");

        Toast.BeginAnimation(OpacityProperty, null);
        if (!S.Animations) { Toast.Opacity = 0; return; }

        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2100))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2500))));
        Toast.BeginAnimation(OpacityProperty, anim);
    }

    // ══════════════════════════════════════════════════════════
    //  TRAY + LIFECYCLE
    // ══════════════════════════════════════════════════════════
    private void SetupTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "VoidClip",
                Visible = false
            };
            var stream = Application.GetResourceStream(new Uri("Assets/voidclip.ico", UriKind.Relative))?.Stream;
            if (stream != null) _tray.Icon = new System.Drawing.Icon(stream);

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Show VoidClip", null, (s, e) => Dispatcher.Invoke(RestoreWindow));
            menu.Items.Add("Clip now", null, (s, e) => Dispatcher.Invoke(() => DoClip(S.DefaultClipSeconds)));
            menu.Items.Add("Stop all sounds", null, (s, e) => Core.Playback.StopAll());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Dispatcher.Invoke(Close));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => Dispatcher.Invoke(RestoreWindow);
        }
        catch (Exception ex) { AppPaths.Log("Tray setup failed: " + ex.Message); }
    }

    private void OnStateChanged(object sender, EventArgs e)
    {
        MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        if (WindowState == WindowState.Minimized && S.MinimizeToTray && _tray != null)
        {
            Hide();
            _tray.Visible = true;
            _tray.BalloonTipTitle = "VoidClip is still listening";
            _tray.BalloonTipText = "Hotkeys stay live. Double-click the tray icon to come back.";
            try { _tray.ShowBalloonTip(1800); } catch { }
        }
        else if (_tray != null && WindowState != WindowState.Minimized)
        {
            _tray.Visible = false;
        }
    }

    private void RestorePlacement()
    {
        if (S.WindowLeft.HasValue && S.WindowTop.HasValue)
        {
            var left = S.WindowLeft.Value;
            var top = S.WindowTop.Value;
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
            // only restore if the saved spot is still on a connected display
            if (left > virtualLeft - 100 && left < virtualRight - 200 &&
                top > virtualTop - 50 && top < virtualBottom - 100)
            {
                Left = left;
                Top = top;
            }
        }
        if (S.WindowWidth > 400) Width = S.WindowWidth;
        if (S.WindowHeight > 300) Height = S.WindowHeight;
        if (S.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            S.WindowLeft = Left; S.WindowTop = Top;
            S.WindowWidth = Width; S.WindowHeight = Height;
        }
        S.WindowMaximized = WindowState == WindowState.Maximized;

        _tick.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }

        SettingsService.Save(S);
        Core.Library.Save();
    }
}
