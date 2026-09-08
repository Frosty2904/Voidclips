using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VoidClip.Audio;
using VoidClip.Models;
using VoidClip.Services;

namespace VoidClip.Views;

public partial class ClipEditor : Window
{
    private readonly Clip _clip;
    private readonly float[] _original;
    private float[] _working;
    private bool _dirtyAudio;
    private bool _ready;
    private ClipVoice _preview;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _previewOffset;   // where in _working the preview started

    private AppSettings S => Core.Settings;

    public ClipEditor(Clip clip)
    {
        InitializeComponent();
        _clip = clip;
        _original = Core.Library.GetSamples(clip);
        _working = (float[])_original.Clone();

        MouseLeftButtonDown += (s, e) => { };
        KeyDown += OnKeyDown;
        Loaded += OnLoaded;
        Closed += (s, e) => { _timer.Stop(); Core.Playback.StopPreview(); };

        _timer.Tick += OnTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NameBox.Text = _clip.Name;
        HeaderName.Text = _clip.Name;
        GainSlider.Value = _clip.Volume;
        LoopChk.IsChecked = _clip.Loop;

        CategoryBox.Items.Add(new ComboBoxItem { Content = "Uncategorised", Tag = "" });
        foreach (var cat in Core.Library.Categories)
            CategoryBox.Items.Add(new ComboBoxItem { Content = cat.Name, Tag = cat.Id });
        CategoryBox.SelectedIndex = Math.Max(0,
            Core.Library.Categories.ToList().FindIndex(c => c.Id == _clip.CategoryId) + 1);

        UpdateHotkeyButton();

        Wave.TrimChanged += (a, b) => UpdateSelectionInfo();
        Wave.Seeked += t => { };

        _ready = true;
        RedrawWave();
        UpdateSelectionInfo();
        UpdateMeta();
        _timer.Start();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel_Click(null, null); return; }
        if (e.Key == Key.Space && NameBox.IsKeyboardFocusWithin == false)
        {
            Play_Click(null, null);
            e.Handled = true;
        }
    }

    // ══════════════════════════════════════════════════════════
    private void RedrawWave()
    {
        Wave.Peaks = Dsp.Peaks(_working, Fmt.Channels, 620);
    }

    private double TotalSeconds => WavIO.Duration(_working);

    private (int start, int end) SelectionSamples()
    {
        var frames = _working.Length / Fmt.Channels;
        var a = (int)(Math.Clamp(Wave.TrimStart, 0, 1) * frames);
        var b = (int)(Math.Clamp(Wave.TrimEnd, 0, 1) * frames);
        if (b <= a) b = Math.Min(frames, a + 1);
        return (a * Fmt.Channels, b * Fmt.Channels);
    }

    private float[] SelectionAudio()
    {
        var (a, b) = SelectionSamples();
        var len = Math.Max(0, b - a);
        var res = new float[len];
        if (len > 0) Array.Copy(_working, a, res, 0, len);
        return res;
    }

    private void UpdateSelectionInfo()
    {
        if (!_ready) return;
        var total = TotalSeconds;
        var s = Wave.TrimStart * total;
        var e = Wave.TrimEnd * total;
        SelectionInfo.Text = $"{s:0.00}s → {e:0.00}s   ({e - s:0.00}s of {total:0.00}s)";
    }

    private void UpdateMeta()
    {
        MetaLine.Text = $"Captured {_clip.Created:dd MMM yyyy, HH:mm} · played {_clip.PlayCount} time"
                        + (_clip.PlayCount == 1 ? "" : "s")
                        + $" · 48 kHz stereo · {TotalSeconds:0.00}s";
    }

    // ══════════════════════════════════════════════════════════
    //  TRANSPORT
    // ══════════════════════════════════════════════════════════
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_preview != null) { StopPreview(); return; }

        var audio = SelectionAudio();
        if (audio.Length == 0) return;
        _previewOffset = Wave.TrimStart;
        _preview = Core.Playback.Preview(audio, (float)GainSlider.Value);
        PlayGlyph.Text = "";
        PlayLabel.Text = "Stop";
    }

    private void StopPreview()
    {
        Core.Playback.StopPreview();
        _preview = null;
        PlayGlyph.Text = "";
        PlayLabel.Text = "Preview selection";
        Wave.Playhead = -1;
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (_preview == null) return;
        var total = TotalSeconds;
        if (total <= 0) return;

        var pos = _previewOffset + _preview.Position / total;
        if (_preview.Position >= _preview.Length - 0.001 || pos > Wave.TrimEnd + 0.001)
        {
            StopPreview();
            return;
        }
        Wave.Playhead = Math.Clamp(pos, 0, 1);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        Wave.TrimStart = 0; Wave.TrimEnd = 1;
        UpdateSelectionInfo();
    }

    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        var audio = SelectionAudio();
        if (audio.Length == 0) return;
        StopPreview();
        _working = audio;
        _dirtyAudio = true;
        Wave.TrimStart = 0; Wave.TrimEnd = 1;
        RedrawWave();
        UpdateSelectionInfo();
        UpdateMeta();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        StopPreview();
        _working = (float[])_original.Clone();
        _dirtyAudio = false;
        FadeInSlider.Value = 0;
        FadeOutSlider.Value = 0;
        Wave.TrimStart = 0; Wave.TrimEnd = 1;
        RedrawWave();
        UpdateSelectionInfo();
        UpdateMeta();
    }

    // ══════════════════════════════════════════════════════════
    //  PROCESSING
    // ══════════════════════════════════════════════════════════
    private void Normalise_Click(object sender, RoutedEventArgs e)
    {
        StopPreview();
        Dsp.Normalize(_working, S.NormalizeTargetDb);
        _dirtyAudio = true;
        RedrawWave();
    }

    private void TrimSilence_Click(object sender, RoutedEventArgs e)
    {
        StopPreview();
        var before = _working.Length;
        _working = Dsp.TrimSilence(_working, Fmt.Channels, Fmt.Rate, S.SilenceThresholdDb);
        if (_working.Length != before)
        {
            _dirtyAudio = true;
            Wave.TrimStart = 0; Wave.TrimEnd = 1;
            RedrawWave();
            UpdateSelectionInfo();
            UpdateMeta();
        }
    }

    private void Fade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        FadeInLabel.Text = $"{FadeInSlider.Value:0} ms";
        FadeOutLabel.Text = $"{FadeOutSlider.Value:0} ms";
    }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        GainLabel.Text = $"{GainSlider.Value * 100:0}%";
        if (_preview != null) _preview.Volume = (float)GainSlider.Value;
    }

    private void Loop_Changed(object sender, RoutedEventArgs e) { }

    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (_ready) HeaderName.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? "Clip" : NameBox.Text;
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e) { }

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

    // ══════════════════════════════════════════════════════════
    //  EXPORT / SAVE / DELETE
    // ══════════════════════════════════════════════════════════
    private void ExportMp3_Click(object sender, RoutedEventArgs e) => Export(ExportFormat.Mp3);
    private void ExportOgg_Click(object sender, RoutedEventArgs e) => Export(ExportFormat.Ogg);

    private void Export(ExportFormat format)
    {
        var audio = BakedAudio();
        if (audio.Length == 0) return;

        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? _clip.Name : NameBox.Text;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export clip",
            FileName = name + Exporter.Extension(format),
            DefaultExt = Exporter.Extension(format),
            Filter = format == ExportFormat.Mp3
                ? "MP3 audio (*.mp3)|*.mp3"
                : "Ogg Vorbis (*.ogg)|*.ogg",
            InitialDirectory = Directory.Exists(S.ExportFolder)
                ? S.ExportFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            Exporter.Export(dlg.FileName, audio, format, S.Mp3Bitrate, S.OggQuality);
            S.ExportFolder = Path.GetDirectoryName(dlg.FileName);
            SettingsService.Save(S);
            if (S.OpenFolderAfterExport)
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\"");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Editor export failed: " + ex);
            Prompt.Info(this, "Export failed", ex.Message);
        }
    }

    /// <summary>The working audio with the current fade settings applied.</summary>
    private float[] BakedAudio()
    {
        var audio = (float[])_working.Clone();
        var fi = (int)FadeInSlider.Value;
        var fo = (int)FadeOutSlider.Value;
        if (fi > 0 || fo > 0) Dsp.Fade(audio, Fmt.Channels, fi, fo, Fmt.Rate);
        return audio;
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StopPreview();

        var fi = (int)FadeInSlider.Value;
        var fo = (int)FadeOutSlider.Value;
        if (_dirtyAudio || fi > 0 || fo > 0)
            Core.Library.ReplaceAudio(_clip, BakedAudio());

        if (!string.IsNullOrWhiteSpace(NameBox.Text)) _clip.Name = NameBox.Text.Trim();
        _clip.Volume = GainSlider.Value;
        _clip.Loop = LoopChk.IsChecked == true;
        if (CategoryBox.SelectedItem is ComboBoxItem item)
            _clip.CategoryId = (string)item.Tag ?? "";

        Core.Library.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StopPreview();
        DialogResult = false;
        Close();
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
