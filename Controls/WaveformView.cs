using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VoidClip.Controls;

/// <summary>
/// Draws cached peak data. Compact mode is the thumbnail on a pad;
/// interactive mode is the trim editor with draggable handles and a playhead.
/// </summary>
public class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(object), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrimStartProperty = DependencyProperty.Register(
        nameof(TrimStart), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrimEndProperty = DependencyProperty.Register(
        nameof(TrimEnd), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
        nameof(Interactive), typeof(bool), typeof(WaveformView), new PropertyMetadata(false));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(WaveformView),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8B, 0x3D, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Peak data (float[]). Typed as object: XAML cannot bind array-typed DPs inside a template.</summary>
    public object Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double TrimStart { get => (double)GetValue(TrimStartProperty); set => SetValue(TrimStartProperty, value); }
    public double TrimEnd { get => (double)GetValue(TrimEndProperty); set => SetValue(TrimEndProperty, value); }
    public double Playhead { get => (double)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public bool Interactive { get => (bool)GetValue(InteractiveProperty); set => SetValue(InteractiveProperty, value); }
    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public event Action<double, double> TrimChanged;
    public event Action<double> Seeked;

    private enum Drag { None, Start, End, Scrub }
    private Drag _drag = Drag.None;

    private static readonly Color CPurple = Color.FromRgb(0x8B, 0x3D, 0xFF);
    private static readonly Color CPink = Color.FromRgb(0xD9, 0x30, 0x8C);
    private static readonly Color CRed = Color.FromRgb(0xFF, 0x2E, 0x63);
    private static readonly Color CBlue = Color.FromRgb(0x3B, 0x82, 0xF6);
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x6A, 0x61, 0x82));
    private static readonly Brush MaskBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x05, 0x04, 0x0A));
    private static readonly Pen HandlePen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x7A)), 1.6);
    private static readonly Pen PlayPen = new(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), 1.6);

    static WaveformView()
    {
        DimBrush.Freeze(); MaskBrush.Freeze(); HandlePen.Freeze(); PlayPen.Freeze();
    }

    public WaveformView()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var peaks = Peaks as float[];
        if (peaks == null || peaks.Length == 0)
        {
            var mid0 = h / 2;
            dc.DrawLine(new Pen(DimBrush, 1), new Point(0, mid0), new Point(w, mid0));
            return;
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(w, 0),
            MappingMode = BrushMappingMode.Absolute
        };
        gradient.GradientStops.Add(new GradientStop(Compact ? CBlue : CPurple, 0));
        gradient.GradientStops.Add(new GradientStop(CPurple, 0.4));
        gradient.GradientStops.Add(new GradientStop(CPink, 0.72));
        gradient.GradientStops.Add(new GradientStop(CRed, 1));
        gradient.Freeze();

        var n = peaks.Length;
        var slot = w / n;
        var barW = Math.Max(1.0, slot * (Compact ? 0.62 : 0.7));
        var mid = h / 2;
        var maxH = h * (Compact ? 0.82 : 0.92);

        for (int i = 0; i < n; i++)
        {
            var a = Math.Clamp(peaks[i], 0f, 1f);
            // gentle curve so quiet detail stays visible
            var scaled = Math.Pow(a, 0.72);
            var bh = Math.Max(Compact ? 1.2 : 2.0, scaled * maxH);
            var x = i * slot + (slot - barW) / 2;
            var rect = new Rect(x, mid - bh / 2, barW, bh);
            var radius = Math.Min(barW / 2, 1.6);
            dc.DrawRoundedRectangle(gradient, null, rect, radius, radius);
        }

        if (Compact) return;

        // ── trim shading ──
        var sx = Math.Clamp(TrimStart, 0, 1) * w;
        var ex = Math.Clamp(TrimEnd, 0, 1) * w;
        if (sx > 0) dc.DrawRectangle(MaskBrush, null, new Rect(0, 0, sx, h));
        if (ex < w) dc.DrawRectangle(MaskBrush, null, new Rect(ex, 0, w - ex, h));

        // ── handles ──
        dc.DrawLine(HandlePen, new Point(sx, 0), new Point(sx, h));
        dc.DrawLine(HandlePen, new Point(ex, 0), new Point(ex, h));
        var grip = new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x7A));
        grip.Freeze();
        dc.DrawRoundedRectangle(grip, null, new Rect(sx - 3, h / 2 - 13, 6, 26), 3, 3);
        dc.DrawRoundedRectangle(grip, null, new Rect(ex - 3, h / 2 - 13, 6, 26), 3, 3);

        // ── playhead ──
        var p = Playhead;
        if (p >= 0 && p <= 1)
        {
            var px = p * w;
            dc.DrawLine(PlayPen, new Point(px, 0), new Point(px, h));
        }
    }

    // ──────────────────────────────────────────────────────────
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!Interactive) return;
        var w = ActualWidth;
        var x = e.GetPosition(this).X;
        var sx = TrimStart * w;
        var ex = TrimEnd * w;

        if (Math.Abs(x - sx) <= 9) _drag = Drag.Start;
        else if (Math.Abs(x - ex) <= 9) _drag = Drag.End;
        else _drag = Drag.Scrub;

        CaptureMouse();
        Update(x);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!Interactive) return;
        var x = e.GetPosition(this).X;

        if (_drag == Drag.None)
        {
            var w = ActualWidth;
            var near = Math.Abs(x - TrimStart * w) <= 9 || Math.Abs(x - TrimEnd * w) <= 9;
            Cursor = near ? Cursors.SizeWE : Cursors.Cross;
            return;
        }
        Update(x);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag != Drag.None) { _drag = Drag.None; ReleaseMouseCapture(); }
    }

    private void Update(double x)
    {
        var w = Math.Max(1, ActualWidth);
        var t = Math.Clamp(x / w, 0, 1);
        const double minGap = 0.004;

        switch (_drag)
        {
            case Drag.Start:
                TrimStart = Math.Min(t, TrimEnd - minGap);
                TrimChanged?.Invoke(TrimStart, TrimEnd);
                break;
            case Drag.End:
                TrimEnd = Math.Max(t, TrimStart + minGap);
                TrimChanged?.Invoke(TrimStart, TrimEnd);
                break;
            case Drag.Scrub:
                Playhead = t;
                Seeked?.Invoke(t);
                break;
        }
    }
}
