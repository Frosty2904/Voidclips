using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VoidClip.Audio;
using VoidClip.Models;
using VoidClip.Services;

namespace VoidClip.Views;

/// <summary>
/// The clip editor: an arrangement of blocks over one or more lanes, each with
/// its own effect chain, mixed down to a single pad when you save.
/// </summary>
public partial class ClipEditor : Window
{
    private enum Target { Block, Track, Master }

    private readonly Clip _clip;
    private readonly float[] _original;
    private readonly EditorProject _project = new();
    private readonly List<SourceEntry> _sources = new();

    private Target _target = Target.Block;
    private TimelineItem _selected;
    private int _activeTrack;
    private bool _ready;

    // ── undo ──
    private readonly List<(string label, EditorProject snapshot)> _undo = new();
    private readonly List<(string label, EditorProject snapshot)> _redo = new();
    private (string label, EditorProject snapshot)? _pendingUndo;
    private const int UndoDepth = 40;

    // ── render / preview ──
    private float[] _mix;
    private bool _mixDirty = true;
    private bool _rendering;
    private Task<float[]> _renderTask;
    private ClipVoice _preview;
    private double _previewStart;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _blockRender = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private readonly HashSet<FxUnit> _collapsed = new();

    private AppSettings S => Core.Settings;

    private sealed class SourceEntry
    {
        public string Name { get; set; } = "";
        public string Sub { get; set; } = "";
        public Clip Clip { get; set; }
        public SourceAudio Source { get; set; }
    }

    // ══════════════════════════════════════════════════════════
    public ClipEditor(Clip clip)
    {
        InitializeComponent();
        _clip = clip;
        _original = Core.Library.GetSamples(clip);

        Loaded += OnLoaded;
        PreviewKeyDown += OnKeyDown;
        Closed += (s, e) => { _timer.Stop(); _blockRender.Stop(); Core.Playback.StopPreview(); };

        _timer.Tick += OnTick;
        _blockRender.Tick += OnBlockRenderTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NameBox.Text = _clip.Name;
        HeaderName.Text = _clip.Name;
        LoopChk.IsChecked = _clip.Loop;
        PadVolume.Value = _clip.Volume;
        PadVolumeLabel.Text = $"{_clip.Volume * 100:0}%";

        CategoryBox.Items.Add(new ComboBoxItem { Content = "Uncategorised", Tag = "" });
        foreach (var cat in Core.Library.Categories)
            CategoryBox.Items.Add(new ComboBoxItem { Content = cat.Name, Tag = cat.Id });
        CategoryBox.SelectedIndex = Math.Max(0,
            Core.Library.Categories.ToList().FindIndex(c => c.Id == _clip.CategoryId) + 1);

        UpdateHotkeyButton();
        BuildProject();
        BuildSourceList();
        FillPresetBox();
        FillEffectBox();

        Timeline.Project = _project;
        Timeline.SelectionChanged += OnBlockSelected;
        Timeline.ItemActivated += item => { _target = Target.Block; UpdateInspector(); };
        Timeline.PlayheadMoved += _ => UpdatePosition();
        Timeline.EditStarting += label => _pendingUndo = (label, _project.Clone());
        Timeline.EditCommitted += () =>
        {
            CommitPendingUndo();
            MarkDirty();
            UpdateInspector();
            Timeline.Refresh();
        };

        BuildTrackHeaders();
        Timeline.Select(_project.Tracks[0].Items.FirstOrDefault());

        _ready = true;
        // the scroll viewer has no width until layout has run, so fit once it does
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Fit_Click(null, null)));
        UpdateInspector();
        UpdateLength();
        _timer.Start();
        Status("Drag blocks to move them, drag their edges to trim.");
    }

    /// <summary>The clip being edited becomes the first block on the first lane.</summary>
    private void BuildProject()
    {
        var voice = new EditorTrack { Name = "Voice" };
        _project.Tracks.Add(voice);
        _project.Tracks.Add(new EditorTrack { Name = "Layer" });

        if (_original.Length == 0) return;

        var source = _project.AddSource(_clip.Name, "this clip", _original, _clip.Id);
        voice.Items.Add(new TimelineItem
        {
            SourceId = source.Id,
            Name = _clip.Name,
            Start = 0,
            TrimIn = 0,
            TrimOut = source.Duration,
        });
    }

    // ══════════════════════════════════════════════════════════
    //  SOURCES
    // ══════════════════════════════════════════════════════════
    private void BuildSourceList()
    {
        _sources.Clear();

        foreach (var s in _project.Sources.Values.OrderBy(s => s.Name))
            _sources.Add(new SourceEntry { Name = s.Name, Sub = $"{s.Origin} · {s.DurationText}", Source = s });

        foreach (var clip in Core.Library.Clips)
        {
            if (clip.Id == _clip.Id) continue;
            if (_sources.Any(e => e.Source?.ClipId == clip.Id)) continue;
            _sources.Add(new SourceEntry
            {
                Name = clip.Name,
                Sub = $"library clip · {clip.DurationText}",
                Clip = clip
            });
        }

        var keep = SourceList.SelectedIndex;
        SourceList.ItemsSource = null;
        SourceList.ItemsSource = _sources;
        if (keep >= 0 && keep < _sources.Count) SourceList.SelectedIndex = keep;
    }

    /// <summary>Loads a source's audio the first time it is actually needed.</summary>
    private SourceAudio Resolve(SourceEntry entry)
    {
        if (entry == null) return null;
        if (entry.Source != null) return entry.Source;
        if (entry.Clip == null) return null;

        var data = Core.Library.GetSamples(entry.Clip);
        if (data.Length == 0)
        {
            Prompt.Info(this, "Missing audio", $"The file for “{entry.Clip.Name}” could not be read.");
            return null;
        }
        entry.Source = _project.AddSource(entry.Clip.Name, "library clip", data, entry.Clip.Id);
        entry.Sub = $"library clip · {entry.Source.DurationText}";
        return entry.Source;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import audio",
            Multiselect = true,
            Filter = "Audio files|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aac;*.wma;*.aiff;*.aif|All files|*.*",
            InitialDirectory = Directory.Exists(S.ExportFolder)
                ? S.ExportFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dlg.ShowDialog(this) != true) return;

        SourceAudio last = null;
        var failed = new List<string>();

        foreach (var path in dlg.FileNames)
        {
            try
            {
                var data = WavIO.Read(path);
                if (data.Length == 0) { failed.Add(Path.GetFileName(path) + " (empty)"); continue; }
                last = _project.AddSource(Path.GetFileNameWithoutExtension(path), "imported file", data);
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Import failed for {path}: {ex}");
                failed.Add(Path.GetFileName(path) + " — " + ex.Message);
            }
        }

        BuildSourceList();

        if (failed.Count > 0)
            Prompt.Info(this, "Some files could not be read",
                string.Join("\n", failed) +
                "\n\nVoidClip reads whatever Windows Media Foundation can decode. " +
                "If a file is DRM-protected or in an unusual codec, convert it to WAV first.");

        if (last != null)
        {
            SourceList.SelectedIndex = _sources.FindIndex(s => s.Source == last);
            Status($"Imported {last.Name} · {last.DurationText}. Drop it on the timeline with Add at playhead.");
        }
    }

    private void GrabBuffer_Click(object sender, RoutedEventArgs e)
    {
        if (!Core.Capture.IsCapturing)
        {
            Prompt.Info(this, "Not capturing",
                "Capture is paused, so the rolling buffer is empty. Start it with Ctrl+Alt+R.");
            return;
        }

        var samples = Core.Capture.Grab(S.DefaultClipSeconds, S.ReactionOffsetMs / 1000.0);
        if (samples.Length == 0)
        {
            Prompt.Info(this, "Buffer empty", "Nothing has come through the capture device yet.");
            return;
        }

        var source = _project.AddSource($"Buffer {DateTime.Now:HH:mm:ss}", "live buffer", samples);
        BuildSourceList();
        SourceList.SelectedIndex = _sources.FindIndex(s => s.Source == source);
        AddSourceToTimeline(source);
    }

    private void SourceList_DoubleClick(object sender, MouseButtonEventArgs e) => AddSource_Click(null, null);

    private void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var source = Resolve(SourceList.SelectedItem as SourceEntry);
        if (source == null)
        {
            Status("Pick a source on the left first.");
            return;
        }
        AddSourceToTimeline(source);
    }

    private void AddSourceToTimeline(SourceAudio source)
    {
        Snapshot("Add block");
        var track = _project.Tracks[Math.Clamp(_activeTrack, 0, _project.Tracks.Count - 1)];
        var item = new TimelineItem
        {
            SourceId = source.Id,
            Name = source.Name,
            Start = Timeline.Playhead,
            TrimIn = 0,
            TrimOut = source.Duration,
        };
        track.Items.Add(item);

        MarkDirty();
        Timeline.Refresh();
        Timeline.Select(item);
        ScheduleBlockRender();
        Status($"Added {source.Name} to {track.Name} at {Mixdown.Time(item.Start)}.");
    }

    // ══════════════════════════════════════════════════════════
    //  TRACKS
    // ══════════════════════════════════════════════════════════
    private void BuildTrackHeaders()
    {
        TrackHeaders.Children.Clear();

        for (int i = 0; i < _project.Tracks.Count; i++)
        {
            var index = i;
            var track = _project.Tracks[i];
            var active = index == _activeTrack;

            var panel = new StackPanel { Margin = new Thickness(9, 6, 7, 0) };

            var name = new TextBlock
            {
                Text = track.Name,
                Style = (Style)FindResource("H2"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = Cursors.Hand,
                ToolTip = "Click to work on this lane · double-click to rename"
            };
            name.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    var newName = Prompt.Text(this, "Rename lane", "", track.Name);
                    if (newName != null) { track.Name = newName; BuildTrackHeaders(); }
                }
                else { _activeTrack = index; BuildTrackHeaders(); }
            };
            panel.Children.Add(name);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

            var mute = new CheckBox { Content = "M", IsChecked = track.Muted, ToolTip = "Mute this lane", FontSize = 11 };
            mute.Click += (s, e) => { track.Muted = mute.IsChecked == true; MarkDirty(); Timeline.Refresh(); };
            buttons.Children.Add(mute);

            var solo = new CheckBox
            {
                Content = "S", IsChecked = track.Solo, Margin = new Thickness(10, 0, 0, 0),
                ToolTip = "Solo this lane", FontSize = 11
            };
            solo.Click += (s, e) => { track.Solo = solo.IsChecked == true; MarkDirty(); Timeline.Refresh(); };
            buttons.Children.Add(solo);

            panel.Children.Add(buttons);

            var gain = new Slider
            {
                Minimum = 0, Maximum = 2, Value = track.Gain, Width = 110,
                Margin = new Thickness(0, 4, 0, 0), ToolTip = "Lane level"
            };
            gain.ValueChanged += (s, e) => { track.Gain = gain.Value; MarkDirty(); };
            panel.Children.Add(gain);

            var fx = new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0),
                Cursor = Cursors.Hand,
                Text = track.Chain.ActiveCount > 0 ? $"lane fx: {track.Chain.ActiveCount}" : "lane fx: none",
                ToolTip = "Edit this lane's effect chain"
            };
            fx.MouseLeftButtonDown += (s, e) => { _activeTrack = index; _target = Target.Track; UpdateInspector(); BuildTrackHeaders(); };
            panel.Children.Add(fx);

            TrackHeaders.Children.Add(new Border
            {
                Height = Timeline.LaneHeight,
                Background = new SolidColorBrush(active ? Color.FromArgb(0x33, 0xA8, 0x55, 0xF7)
                                                        : Color.FromArgb(0x22, 0x0B, 0x08, 0x13)),
                BorderBrush = (Brush)FindResource("StrokeSoft"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = panel
            });
        }
    }

    private void AddTrack_Click(object sender, RoutedEventArgs e)
    {
        Snapshot("Add lane");
        _project.Tracks.Add(new EditorTrack { Name = "Lane " + (_project.Tracks.Count + 1) });
        _activeTrack = _project.Tracks.Count - 1;
        BuildTrackHeaders();
        Timeline.Refresh();
    }

    // ══════════════════════════════════════════════════════════
    //  BLOCK COMMANDS
    // ══════════════════════════════════════════════════════════
    private void OnBlockSelected(TimelineItem item)
    {
        _selected = item;
        if (item != null)
        {
            var track = _project.TrackOf(item);
            if (track != null)
            {
                _activeTrack = _project.Tracks.IndexOf(track);
                BuildTrackHeaders();
            }
        }
        UpdateInspector();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { Status("Select a block to split."); return; }

        var at = Timeline.Playhead;
        if (at <= _selected.Start + 0.02 || at >= _selected.End - 0.02)
        {
            Status("Put the playhead inside the selected block first.");
            return;
        }

        Snapshot("Split block");

        // effects can stretch a block, so map the cut back into source time
        var ratio = _selected.Length > 0 ? _selected.RawLength / _selected.Length : 1;
        var cutInSource = _selected.TrimIn + (at - _selected.Start) * ratio;

        var right = _selected.Clone();
        right.Id = Guid.NewGuid().ToString("N");
        right.TrimIn = cutInSource;
        right.Start = at;
        right.FadeIn = 0;
        right.Invalidate();

        _selected.TrimOut = cutInSource;
        _selected.FadeOut = 0;
        _selected.Invalidate();

        var track = _project.TrackOf(_selected) ?? _project.Tracks[0];
        track.Items.Insert(track.Items.IndexOf(_selected) + 1, right);

        MarkDirty();
        ScheduleBlockRender();
        Timeline.Refresh();
        Timeline.Select(right);
        Status("Split.");
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { Status("Select a block to duplicate."); return; }
        Snapshot("Duplicate block");

        var copy = _selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Start = _selected.End;

        var track = _project.TrackOf(_selected) ?? _project.Tracks[0];
        track.Items.Add(copy);

        MarkDirty();
        Timeline.Refresh();
        Timeline.Select(copy);
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { Status("Select a block to remove."); return; }
        Snapshot("Remove block");

        var track = _project.TrackOf(_selected);
        track?.Items.Remove(_selected);

        Timeline.Select(null);
        MarkDirty();
        Timeline.Refresh();
    }

    private void TrimToPlayhead_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var at = Timeline.Playhead;
        if (at <= _selected.Start || at >= _selected.End) { Status("Playhead is not inside the block."); return; }

        Snapshot("Trim block");
        var ratio = _selected.Length > 0 ? _selected.RawLength / _selected.Length : 1;
        _selected.TrimIn += (at - _selected.Start) * ratio;
        _selected.Start = at;

        MarkDirty();
        ScheduleBlockRender();
        UpdateInspector();
        Timeline.Refresh();
    }

    /// <summary>Pulls the block's in and out points in to the audible part of the source.</summary>
    private void TrimSilence_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { Status("Select a block first."); return; }
        var source = _project.Source(_selected.SourceId);
        if (source == null || source.Data.Length == 0) return;

        var total = source.Data.Length;
        var from = Math.Clamp(Mixdown.ToSamples(_selected.TrimIn), 0, total);
        var to = _selected.TrimOut <= _selected.TrimIn
            ? total
            : Math.Clamp(Mixdown.ToSamples(_selected.TrimOut), from, total);

        if (!Dsp.SilenceBounds(source.Data, from, to - from, Fmt.Channels, Fmt.Rate,
                               S.SilenceThresholdDb, 120, out var first, out var last))
        {
            Status($"Nothing in that block is above {S.SilenceThresholdDb:0.#} dBFS.");
            return;
        }
        if (first == from && last == to) { Status("No dead air to cut off that one."); return; }

        Snapshot("Trim silence");
        var before = _selected.RawLength;
        _selected.TrimIn = Mixdown.ToSeconds(first);
        _selected.TrimOut = Mixdown.ToSeconds(last);

        MarkDirty();
        ScheduleBlockRender();
        UpdateBlockMeta();
        Timeline.Refresh();
        Status($"Trimmed {before:0.00}s down to {_selected.RawLength:0.00}s.");
    }

    /// <summary>
    /// Normalising is a Gain effect with its normalise switch on, rather than a
    /// one-off change to the audio, so it stays visible in the chain and can be
    /// tweaked or bypassed later.
    /// </summary>
    private void Normalise_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { Status("Select a block first."); return; }

        Snapshot("Normalise block");
        var gain = _selected.Chain.Units.FirstOrDefault(u => u.Type == FxType.Gain);
        if (gain == null)
        {
            gain = new FxUnit(FxType.Gain);
            _selected.Chain.Units.Add(gain);
        }
        gain.Enabled = true;
        gain.Put("normalise", 1);
        gain.Put("gain", 0);
        gain.Put("ceiling", Math.Max(-12, S.NormalizeTargetDb));

        _target = Target.Block;
        InvalidateChain();
        UpdateInspector();
        BuildRack();
        BuildTrackHeaders();
        Status($"Normalising to {Math.Max(-12, S.NormalizeTargetDb):0.#} dBFS — it is the Gain effect in the chain.");
    }

    private void BlockGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        BlockGainLabel.Text = $"{BlockGain.Value * 100:0}%";
        if (_selected == null) return;
        _selected.Gain = BlockGain.Value;
        MarkDirty();
    }

    private void BlockFade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        BlockFadeInLabel.Text = $"{BlockFadeIn.Value:0} ms";
        BlockFadeOutLabel.Text = $"{BlockFadeOut.Value:0} ms";
        if (_selected == null) return;
        _selected.FadeIn = BlockFadeIn.Value / 1000.0;
        _selected.FadeOut = BlockFadeOut.Value / 1000.0;
        MarkDirty();
        Timeline.Refresh();
    }

    private void BlockMute_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _selected == null) return;
        _selected.Muted = BlockMute.IsChecked == true;
        MarkDirty();
        Timeline.Refresh();
    }

    // ══════════════════════════════════════════════════════════
    //  INSPECTOR
    // ══════════════════════════════════════════════════════════
    private FxChain CurrentChain => _target switch
    {
        Target.Block => _selected?.Chain,
        Target.Track => _project.Tracks.ElementAtOrDefault(_activeTrack)?.Chain,
        _ => _project.Master
    };

    private void TargetBlock_Click(object sender, RoutedEventArgs e) { _target = Target.Block; UpdateInspector(); }
    private void TargetTrack_Click(object sender, RoutedEventArgs e) { _target = Target.Track; UpdateInspector(); }
    private void TargetMaster_Click(object sender, RoutedEventArgs e) { _target = Target.Master; UpdateInspector(); }

    /// <summary>The chain the rack is currently showing, so it is only rebuilt when that changes.</summary>
    private FxChain _rackChain;

    private void UpdateInspector()
    {
        if (!IsLoaded) return;

        TargetBlockBtn.Style = (Style)FindResource(_target == Target.Block ? "AccentButton" : "GhostButton");
        TargetTrackBtn.Style = (Style)FindResource(_target == Target.Track ? "AccentButton" : "GhostButton");
        TargetMasterBtn.Style = (Style)FindResource(_target == Target.Master ? "AccentButton" : "GhostButton");

        BlockPanel.Visibility = _target == Target.Block ? Visibility.Visible : Visibility.Collapsed;

        if (_selected != null)
        {
            UpdateBlockMeta();

            var wasReady = _ready;
            _ready = false;
            BlockGain.Value = _selected.Gain;
            BlockFadeIn.Value = _selected.FadeIn * 1000;
            BlockFadeOut.Value = _selected.FadeOut * 1000;
            BlockMute.IsChecked = _selected.Muted;
            _ready = wasReady;

            BlockGainLabel.Text = $"{_selected.Gain * 100:0}%";
            BlockFadeInLabel.Text = $"{_selected.FadeIn * 1000:0} ms";
            BlockFadeOutLabel.Text = $"{_selected.FadeOut * 1000:0} ms";
        }
        else
        {
            UpdateBlockMeta();
        }

        RackTitle.Text = _target switch
        {
            Target.Block => _selected == null ? "BLOCK CHAIN — NOTHING SELECTED" : "BLOCK CHAIN",
            Target.Track => $"LANE CHAIN — {_project.Tracks.ElementAtOrDefault(_activeTrack)?.Name?.ToUpperInvariant()}",
            _ => "MASTER CHAIN"
        };

        var chain = CurrentChain;
        if (!ReferenceEquals(chain, _rackChain))
        {
            _rackChain = chain;
            BuildRack();
        }
    }

    /// <summary>
    /// Refreshes just the read-outs about the selected block. Kept apart from
    /// <see cref="UpdateInspector"/> so a background render never rebuilds the
    /// rack while someone is dragging one of its sliders.
    /// </summary>
    private void UpdateBlockMeta()
    {
        if (!IsLoaded) return;

        if (_selected == null)
        {
            BlockTitle.Text = "No block selected";
            BlockMeta.Text = "Click a block on the timeline to work on it.";
            return;
        }

        var source = _project.Source(_selected.SourceId);
        BlockTitle.Text = _selected.Name;
        BlockMeta.Text = $"{Mixdown.Time(_selected.Start)} → {Mixdown.Time(_selected.End)}"
                       + $"  ·  {_selected.Length:0.00}s"
                       + (source != null ? $"  ·  from {source.Name}" : "");
    }

    // ══════════════════════════════════════════════════════════
    //  THE EFFECT RACK
    // ══════════════════════════════════════════════════════════
    private void BuildRack()
    {
        RackPanel.Children.Clear();
        var chain = _rackChain = CurrentChain;

        if (chain == null)
        {
            RackPanel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                Text = "Select a block on the timeline, or point the rack at the lane or the master.",
                Margin = new Thickness(2, 4, 2, 0)
            });
            return;
        }

        if (chain.Units.Count == 0)
        {
            RackPanel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                Text = "Nothing here yet. Apply a preset, or add an effect above — they run top to bottom.",
                Margin = new Thickness(2, 4, 2, 0)
            });
            return;
        }

        foreach (var unit in chain.Units.ToList())
            RackPanel.Children.Add(BuildUnitCard(unit, chain));
    }

    private Border BuildUnitCard(FxUnit unit, FxChain chain)
    {
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var collapsed = _collapsed.Contains(unit);
        body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        // ── header ──
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = new CheckBox { IsChecked = unit.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Bypass" };
        enabled.Click += (s, e) =>
        {
            unit.Enabled = enabled.IsChecked == true;
            InvalidateChain();
        };
        Grid.SetColumn(enabled, 0);
        header.Children.Add(enabled);

        var titles = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
        titles.Children.Add(new TextBlock
        {
            Text = unit.Name,
            Style = (Style)FindResource("H2"),
            FontSize = 12.5
        });
        var summary = new TextBlock
        {
            Text = unit.Summary(),
            Style = (Style)FindResource("Hint"),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        titles.Children.Add(summary);
        titles.MouseLeftButtonDown += (s, e) =>
        {
            if (_collapsed.Contains(unit)) _collapsed.Remove(unit); else _collapsed.Add(unit);
            body.Visibility = _collapsed.Contains(unit) ? Visibility.Collapsed : Visibility.Visible;
        };
        titles.ToolTip = unit.Info.Blurb;
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(RackButton("", "Move up", () =>
        {
            var i = chain.Units.IndexOf(unit);
            if (i <= 0) return;
            Snapshot("Reorder effects");
            chain.Units.Move(i, i - 1);
            InvalidateChain();
            BuildRack();
        }));
        tools.Children.Add(RackButton("", "Move down", () =>
        {
            var i = chain.Units.IndexOf(unit);
            if (i < 0 || i >= chain.Units.Count - 1) return;
            Snapshot("Reorder effects");
            chain.Units.Move(i, i + 1);
            InvalidateChain();
            BuildRack();
        }));
        tools.Children.Add(RackButton("", "Remove effect", () =>
        {
            Snapshot("Remove effect");
            chain.Units.Remove(unit);
            _collapsed.Remove(unit);
            InvalidateChain();
            BuildRack();
            BuildTrackHeaders();
        }));
        Grid.SetColumn(tools, 2);
        header.Children.Add(tools);

        // ── parameters ──
        foreach (var p in unit.Info.Params)
            body.Children.Add(BuildParamRow(unit, p, summary));

        if (unit.Info.Params.Length == 0)
            body.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                FontSize = 10.5,
                Text = unit.Info.Blurb
            });
        else
            body.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"),
                FontSize = 10,
                Opacity = 0.75,
                Margin = new Thickness(0, 8, 0, 0),
                Text = unit.Info.Blurb
            });

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);

        return new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x13, 0x0F, 0x22)),
            BorderBrush = (Brush)FindResource("StrokeSoft"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 9, 11, 10),
            Margin = new Thickness(0, 0, 4, 8),
            Child = stack
        };
    }

    private Button RackButton(string glyph, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = tip
        };
        b.Click += (s, e) => onClick();
        return b;
    }

    private UIElement BuildParamRow(FxUnit unit, FxParam p, TextBlock summary)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

        var label = new TextBlock
        {
            Text = p.Label,
            Style = (Style)FindResource("Body"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Cursor = Cursors.Hand,
            ToolTip = "Double-click to put this back to its default"
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var value = new TextBlock
        {
            Text = p.Display(unit.Get(p.Key)),
            Style = (Style)FindResource("Hint"),
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        switch (p.Kind)
        {
            case FxParamKind.Toggle:
            {
                var chk = new CheckBox
                {
                    IsChecked = unit.On(p.Key),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                chk.Click += (s, e) =>
                {
                    unit.Put(p.Key, chk.IsChecked == true ? 1 : 0);
                    value.Text = p.Display(unit.Get(p.Key));
                    summary.Text = unit.Summary();
                    InvalidateChain();
                };
                Grid.SetColumn(chk, 1);
                grid.Children.Add(chk);
                break;
            }
            case FxParamKind.Choice:
            {
                var combo = new ComboBox { VerticalAlignment = VerticalAlignment.Center, Height = 28, FontSize = 11.5 };
                foreach (var c in p.Choices) combo.Items.Add(c);
                combo.SelectedIndex = (int)Math.Clamp(unit.Get(p.Key), 0, p.Choices.Length - 1);
                combo.SelectionChanged += (s, e) =>
                {
                    if (combo.SelectedIndex < 0) return;
                    unit.Put(p.Key, combo.SelectedIndex);
                    value.Text = p.Display(unit.Get(p.Key));
                    summary.Text = unit.Summary();
                    InvalidateChain();
                };
                Grid.SetColumn(combo, 1);
                grid.Children.Add(combo);
                value.Visibility = Visibility.Collapsed;
                Grid.SetColumnSpan(combo, 2);
                break;
            }
            default:
            {
                var slider = new Slider
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = p.ToSlider(unit.Get(p.Key)),
                    VerticalAlignment = VerticalAlignment.Center,
                    SmallChange = 0.01,
                    LargeChange = 0.1
                };
                slider.ValueChanged += (s, e) =>
                {
                    var v = p.FromSlider(slider.Value);
                    unit.Put(p.Key, v);
                    value.Text = p.Display(v);
                    summary.Text = unit.Summary();
                    InvalidateChain();
                };
                label.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount != 2) return;
                    slider.Value = p.ToSlider(p.Default);
                };
                Grid.SetColumn(slider, 1);
                grid.Children.Add(slider);
                break;
            }
        }

        return grid;
    }

    /// <summary>A chain changed: the affected audio needs re-rendering and the mix is stale.</summary>
    private void InvalidateChain()
    {
        // Only a block's own chain feeds its cache; lane and master effects run
        // after the blocks are laid down, so those caches stay good.
        if (_target == Target.Block) _selected?.Invalidate();

        MarkDirty();
        ScheduleBlockRender();
    }

    // ── presets and the add box ──
    private void FillPresetBox()
    {
        PresetBox.Items.Clear();
        foreach (var preset in FxPresets.All)
            PresetBox.Items.Add(new ComboBoxItem { Content = $"{preset.Group} · {preset.Name}", Tag = preset });
        PresetBox.SelectedIndex = 0;
    }

    private void FillEffectBox()
    {
        AddFxBox.Items.Clear();
        foreach (var info in FxCatalog.All.OrderBy(i => i.Group).ThenBy(i => i.Name))
            AddFxBox.Items.Add(new ComboBoxItem { Content = $"{info.Group} · {info.Name}", Tag = info });
        AddFxBox.SelectedIndex = 0;
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedItem is ComboBoxItem { Tag: FxPreset preset })
            PresetBlurb.Text = preset.Blurb;
    }

    private void AddFxBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AddFxBox.SelectedItem is ComboBoxItem { Tag: FxInfo info })
            AddFxBlurb.Text = info.Blurb;
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        var chain = CurrentChain;
        if (chain == null) { Status("Point the rack at a block, a lane or the master first."); return; }
        if (PresetBox.SelectedItem is not ComboBoxItem { Tag: FxPreset preset }) return;

        Snapshot("Apply preset");
        chain.Units.Clear();
        foreach (var unit in preset.Build()) chain.Units.Add(unit);

        InvalidateChain();
        BuildRack();
        BuildTrackHeaders();
        Status($"{preset.Name}: {chain.Describe()}");
    }

    private void AddFx_Click(object sender, RoutedEventArgs e)
    {
        var chain = CurrentChain;
        if (chain == null) { Status("Point the rack at a block, a lane or the master first."); return; }
        if (AddFxBox.SelectedItem is not ComboBoxItem { Tag: FxInfo info }) return;

        Snapshot("Add effect");
        chain.Units.Add(new FxUnit(info.Type));
        InvalidateChain();
        BuildRack();
        BuildTrackHeaders();
    }

    private void ClearChain_Click(object sender, RoutedEventArgs e)
    {
        var chain = CurrentChain;
        if (chain == null || chain.Units.Count == 0) return;

        Snapshot("Clear chain");
        chain.Units.Clear();
        InvalidateChain();
        BuildRack();
        BuildTrackHeaders();
    }

    // ══════════════════════════════════════════════════════════
    //  RENDERING
    // ══════════════════════════════════════════════════════════
    private void MarkDirty()
    {
        _mixDirty = true;
        UpdateLength();
    }

    private void ScheduleBlockRender()
    {
        _blockRender.Stop();
        _blockRender.Start();
    }

    private async void OnBlockRenderTick(object sender, EventArgs e)
    {
        _blockRender.Stop();
        if (_rendering) { ScheduleBlockRender(); return; }

        _rendering = true;
        Status("Rendering…");
        try
        {
            await Task.Run(() => Mixdown.RenderAll(_project));
        }
        catch (Exception ex)
        {
            AppPaths.Log("Block render failed: " + ex);
        }
        _rendering = false;

        Timeline.Refresh();
        UpdateLength();
        UpdateBlockMeta();
        Status("");
    }

    private async Task<float[]> RenderMixAsync()
    {
        if (!_mixDirty && _mix != null) return _mix;

        // two things can ask for the mix at once — a second Play while the first
        // render is still going, say — and they should share the one render
        if (_renderTask != null) return await _renderTask;

        _rendering = true;
        Status("Rendering mix…");
        try
        {
            var project = _project;
            _renderTask = Task.Run(() => Mixdown.Render(project));
            _mix = await _renderTask;
            _mixDirty = false;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Mixdown failed: " + ex);
            Prompt.Info(this, "Could not render", ex.Message);
            _mix = Array.Empty<float>();
        }
        finally
        {
            _renderTask = null;
            _rendering = false;
            Status("");
        }

        Timeline.Refresh();
        UpdateLength();
        UpdateBlockMeta();
        return _mix;
    }

    private void UpdateLength()
    {
        var total = _project.Duration;
        LengthText.Text = "  / " + Mixdown.Time(total);
    }

    private void UpdatePosition() => PositionText.Text = Mixdown.Time(Timeline.Playhead);

    private void Status(string text) => StatusText.Text = text;

    // ══════════════════════════════════════════════════════════
    //  TRANSPORT
    // ══════════════════════════════════════════════════════════
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_preview != null) { StopPreview(); return; }

        var mix = await RenderMixAsync();
        if (mix == null || mix.Length == 0)
        {
            Status("Nothing to play — the timeline is empty.");
            return;
        }

        var start = Timeline.Playhead;
        if (start >= Mixdown.ToSeconds(mix.Length) - 0.02) start = 0;

        var from = Math.Clamp(Mixdown.ToSamples(start), 0, mix.Length);
        var slice = new float[mix.Length - from];
        Array.Copy(mix, from, slice, 0, slice.Length);

        _previewStart = start;
        _preview = Core.Playback.Preview(slice);
        if (_preview == null) return;

        PlayGlyph.Text = "";
        PlayLabel.Text = "Stop";
    }

    private void StopPreview()
    {
        Core.Playback.StopPreview();
        _preview = null;
        PlayGlyph.Text = "";
        PlayLabel.Text = "Play mix";
    }

    private void ToStart_Click(object sender, RoutedEventArgs e)
    {
        Timeline.Playhead = 0;
        UpdatePosition();
        TimelineScroll.ScrollToHorizontalOffset(0);
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (_preview == null) return;

        var position = _previewStart + _preview.Position;
        if (_preview.Position >= _preview.Length - 0.001)
        {
            StopPreview();
            if (LoopPreviewChk.IsChecked == true)
            {
                Timeline.Playhead = 0;
                Play_Click(null, null);
            }
            return;
        }

        Timeline.Playhead = position;
        UpdatePosition();

        // keep the playhead on screen without fighting the user's own scrolling
        var x = Timeline.XOf(position);
        var left = TimelineScroll.HorizontalOffset;
        var right = left + TimelineScroll.ViewportWidth;
        if (x < left || x > right - 40) TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 60));
    }

    // ══════════════════════════════════════════════════════════
    //  VIEW
    // ══════════════════════════════════════════════════════════
    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        Timeline.Zoom = ZoomSlider.Value;
        Timeline.Refresh();
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        var width = Math.Max(200, TimelineScroll.ActualWidth - 24);
        var seconds = Math.Max(2, _project.Duration + 1);
        ZoomSlider.Value = Math.Clamp(width / seconds, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    private void Snap_Changed(object sender, RoutedEventArgs e) => Timeline.Snap = SnapChk.IsChecked == true;

    private void Timeline_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        // zoom about the pointer, so what you are looking at stays put
        var before = e.GetPosition(Timeline).X / Math.Max(1, Timeline.Zoom);
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value * (e.Delta > 0 ? 1.18 : 1 / 1.18),
                                      ZoomSlider.Minimum, ZoomSlider.Maximum);
        Timeline.Zoom = ZoomSlider.Value;
        Timeline.UpdateLayout();
        TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, before * Timeline.Zoom - e.GetPosition(TimelineScroll).X));
        e.Handled = true;
    }

    // ══════════════════════════════════════════════════════════
    //  UNDO
    // ══════════════════════════════════════════════════════════
    private void Snapshot(string label)
    {
        _undo.Add((label, _project.Clone()));
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
        _pendingUndo = null;
    }

    private void CommitPendingUndo()
    {
        if (_pendingUndo == null) return;
        _undo.Add(_pendingUndo.Value);
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
        _pendingUndo = null;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) { Status("Nothing to undo."); return; }
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((entry.label, _project.Clone()));
        Restore(entry.snapshot);
        Status("Undid: " + entry.label.ToLowerInvariant());
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) { Status("Nothing to redo."); return; }
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((entry.label, _project.Clone()));
        Restore(entry.snapshot);
        Status("Redid: " + entry.label.ToLowerInvariant());
    }

    private void Restore(EditorProject snapshot)
    {
        var selectedId = _selected?.Id;
        _project.RestoreFrom(snapshot);

        _activeTrack = Math.Clamp(_activeTrack, 0, Math.Max(0, _project.Tracks.Count - 1));
        _selected = null;
        Timeline.SelectedItem = null;

        BuildTrackHeaders();
        BuildSourceList();
        Timeline.Refresh();

        var again = _project.AllItems.FirstOrDefault(i => i.Id == selectedId);
        Timeline.Select(again);

        MarkDirty();
        ScheduleBlockRender();
        UpdateInspector();
    }

    // ══════════════════════════════════════════════════════════
    //  KEYS
    // ══════════════════════════════════════════════════════════
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var typing = NameBox.IsKeyboardFocusWithin;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (e.Key == Key.Escape) { Cancel_Click(null, null); e.Handled = true; return; }
        if (typing) return;

        if (ctrl && e.Key == Key.Z) { Undo_Click(null, null); e.Handled = true; }
        else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))))
        { Redo_Click(null, null); e.Handled = true; }
        else if (ctrl && e.Key == Key.I) { Import_Click(null, null); e.Handled = true; }
        else if (ctrl && e.Key == Key.D) { Duplicate_Click(null, null); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { Save_Click(null, null); e.Handled = true; }
        else if (e.Key == Key.Space) { Play_Click(null, null); e.Handled = true; }
        else if (e.Key == Key.S) { Split_Click(null, null); e.Handled = true; }
        else if (e.Key is Key.Delete or Key.Back) { RemoveItem_Click(null, null); e.Handled = true; }
        else if (e.Key == Key.Home) { ToStart_Click(null, null); e.Handled = true; }
    }

    // ══════════════════════════════════════════════════════════
    //  CLIP-LEVEL BITS
    // ══════════════════════════════════════════════════════════
    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) HeaderName.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? "Clip" : NameBox.Text;
    }

    private void PadVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) PadVolumeLabel.Text = $"{PadVolume.Value * 100:0}%";
    }

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        var g = HotkeyDialog.Capture(this, "Hotkey for this clip", _clip.Hotkey);
        if (g == null) return;
        _clip.Hotkey = g;
        UpdateHotkeyButton();
    }

    private void UpdateHotkeyButton()
        => HotkeyBtn.Content = string.IsNullOrWhiteSpace(_clip.Hotkey)
            ? "Not set — click to assign"
            : HotkeyService.Pretty(_clip.Hotkey);

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ══════════════════════════════════════════════════════════
    //  EXPORT / SAVE / DELETE
    // ══════════════════════════════════════════════════════════
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var mix = await RenderMixAsync();
        if (mix == null || mix.Length == 0) { Status("Nothing to export."); return; }

        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? _clip.Name : NameBox.Text;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export clip",
            FileName = name + Exporter.Extension(S.DefaultExportFormat),
            DefaultExt = Exporter.Extension(S.DefaultExportFormat),
            Filter = "MP3 audio (*.mp3)|*.mp3|Ogg Vorbis (*.ogg)|*.ogg|WAV audio (*.wav)|*.wav",
            FilterIndex = S.DefaultExportFormat switch
            {
                ExportFormat.Ogg => 2,
                ExportFormat.Wav => 3,
                _ => 1
            },
            InitialDirectory = Directory.Exists(S.ExportFolder)
                ? S.ExportFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dlg.ShowDialog(this) != true) return;

        var format = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
        {
            ".ogg" => ExportFormat.Ogg,
            ".wav" => ExportFormat.Wav,
            _ => ExportFormat.Mp3
        };

        try
        {
            Exporter.Export(dlg.FileName, mix, format, S.Mp3Bitrate, S.OggQuality);
            S.ExportFolder = Path.GetDirectoryName(dlg.FileName);
            SettingsService.Save(S);
            if (S.OpenFolderAfterExport)
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\"");
            Status("Exported " + Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            AppPaths.Log("Editor export failed: " + ex);
            Prompt.Info(this, "Export failed", ex.Message);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!Prompt.Confirm(this, "Delete clip",
            $"“{_clip.Name}” and its audio file will be removed. This cannot be undone.",
            "Delete", danger: true)) return;

        StopPreview();
        Core.Playback.StopClip(_clip.Id);
        Core.Hotkeys.Unregister("clip:" + _clip.Id);
        Core.Library.Delete(_clip);
        DialogResult = false;
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveInternal(asNew: false);

    private async void SaveAsNew_Click(object sender, RoutedEventArgs e) => await SaveInternal(asNew: true);

    private async Task SaveInternal(bool asNew)
    {
        StopPreview();

        var mix = await RenderMixAsync();
        if (mix == null || mix.Length == 0)
        {
            Prompt.Info(this, "Nothing to save",
                "The timeline is empty, so there is no audio to write. Add a block first.");
            return;
        }

        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? _clip.Name : NameBox.Text.Trim();
        var categoryId = CategoryBox.SelectedItem is ComboBoxItem item ? (string)item.Tag ?? "" : _clip.CategoryId;

        if (asNew)
        {
            var copy = Core.Library.AddClip(mix, Core.Library.NextClipName(name + " (edit)"), categoryId);
            copy.Loop = LoopChk.IsChecked == true;
            copy.Volume = PadVolume.Value;
            Core.Library.Save();
            Status("Saved as " + copy.Name);
        }
        else
        {
            Core.Library.ReplaceAudio(_clip, mix);
            _clip.Name = name;
            _clip.Loop = LoopChk.IsChecked == true;
            _clip.Volume = PadVolume.Value;
            _clip.CategoryId = categoryId;
            Core.Library.Save();
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StopPreview();
        DialogResult = false;
        Close();
    }



}
