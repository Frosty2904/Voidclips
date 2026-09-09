using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VoidClip.Models;

namespace VoidClip.Controls;

/// <summary>
/// The arrangement view: a ruler, one lane per track, and a block for every
/// piece of audio in it. Blocks can be dragged along the timeline, dropped on
/// another lane, and trimmed by their edges.
/// </summary>
public class TimelineView : FrameworkElement
{
    public const double RulerHeight = 26;

    // ── dependency properties ──
    public static readonly DependencyProperty ProjectProperty = DependencyProperty.Register(
        nameof(Project), typeof(EditorProject), typeof(TimelineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(TimelineView),
        new FrameworkPropertyMetadata(80.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LaneHeightProperty = DependencyProperty.Register(
        nameof(LaneHeight), typeof(double), typeof(TimelineView),
        new FrameworkPropertyMetadata(78.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(double), typeof(TimelineView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(TimelineItem), typeof(TimelineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SnapProperty = DependencyProperty.Register(
        nameof(Snap), typeof(bool), typeof(TimelineView), new PropertyMetadata(true));

    public EditorProject Project { get => (EditorProject)GetValue(ProjectProperty); set => SetValue(ProjectProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double LaneHeight { get => (double)GetValue(LaneHeightProperty); set => SetValue(LaneHeightProperty, value); }
    public double Playhead { get => (double)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public TimelineItem SelectedItem { get => (TimelineItem)GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public bool Snap { get => (bool)GetValue(SnapProperty); set => SetValue(SnapProperty, value); }

    // ── events ──
    public event Action<TimelineItem> SelectionChanged;
    public event Action<TimelineItem> ItemActivated;
    public event Action<double> PlayheadMoved;
    /// <summary>Raised before a drag changes anything, so the editor can take an undo snapshot.</summary>
    public event Action<string> EditStarting;
    /// <summary>Raised once a drag finishes and the arrangement actually changed.</summary>
    public event Action EditCommitted;

    // ── palette ──
    private static readonly Color[] BlockColors =
    {
        Color.FromRgb(0x8B, 0x3D, 0xFF),
        Color.FromRgb(0xFF, 0x2E, 0x63),
        Color.FromRgb(0x3B, 0x82, 0xF6),
        Color.FromRgb(0xD9, 0x30, 0x8C),
        Color.FromRgb(0x18, 0xB5, 0x9C),
        Color.FromRgb(0xF5, 0x9E, 0x0B),
    };

    private static readonly Brush LaneA = new SolidColorBrush(Color.FromArgb(0x40, 0x0B, 0x08, 0x13));
    private static readonly Brush LaneB = new SolidColorBrush(Color.FromArgb(0x66, 0x0B, 0x08, 0x13));
    private static readonly Brush RulerBg = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x0C, 0x1B));
    private static readonly Brush RulerText = new SolidColorBrush(Color.FromRgb(0x8A, 0x82, 0xA6));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(0x33, 0x6A, 0x61, 0x82)), 1);
    private static readonly Pen GridPenFaint = new(new SolidColorBrush(Color.FromArgb(0x18, 0x6A, 0x61, 0x82)), 1);
    private static readonly Pen LaneLine = new(new SolidColorBrush(Color.FromArgb(0x55, 0x6A, 0x61, 0x82)), 1);
    private static readonly Pen PlayPen = new(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), 1.6);
    private static readonly Pen SelectPen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 1.6);
    private static readonly Pen SnapPen = new(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0x5C, 0x7A)), 1);
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0xE9, 0xF5));
    private static readonly Brush MutedWash = new SolidColorBrush(Color.FromArgb(0x99, 0x0B, 0x08, 0x13));
    private static readonly Typeface Face = new("Segoe UI");

    static TimelineView()
    {
        LaneA.Freeze(); LaneB.Freeze(); RulerBg.Freeze(); RulerText.Freeze();
        GridPen.Freeze(); GridPenFaint.Freeze(); LaneLine.Freeze(); PlayPen.Freeze();
        SelectPen.Freeze(); SnapPen.Freeze(); TextBrush.Freeze(); MutedWash.Freeze();
    }

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    // ══════════════════════════════════════════════════════════
    //  GEOMETRY
    // ══════════════════════════════════════════════════════════
    public double ContentSeconds => Math.Max(8, (Project?.Duration ?? 0) + 4);

    protected override Size MeasureOverride(Size availableSize)
    {
        var tracks = Project?.Tracks.Count ?? 0;
        var w = ContentSeconds * Zoom;
        var h = RulerHeight + Math.Max(1, tracks) * LaneHeight + 8;
        return new Size(double.IsInfinity(availableSize.Width) ? w : Math.Max(w, 0), h);
    }

    public double XOf(double seconds) => seconds * Zoom;
    public double SecondsAt(double x) => Math.Max(0, x / Math.Max(1, Zoom));
    public double LaneTop(int index) => RulerHeight + index * LaneHeight;

    public int LaneAt(double y)
    {
        if (Project == null || Project.Tracks.Count == 0) return 0;
        var idx = (int)((y - RulerHeight) / LaneHeight);
        return Math.Clamp(idx, 0, Project.Tracks.Count - 1);
    }

    private static Color ColorFor(TimelineItem item)
    {
        var hash = 0;
        foreach (var c in item.SourceId) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return BlockColors[hash % BlockColors.Length];
    }

    public Rect RectOf(TimelineItem item, int laneIndex)
    {
        var x = XOf(item.Start);
        var w = Math.Max(3, XOf(item.Length));
        var y = LaneTop(laneIndex) + 4;
        return new Rect(x, y, w, LaneHeight - 10);
    }

    // ══════════════════════════════════════════════════════════
    //  RENDER
    // ══════════════════════════════════════════════════════════
    protected override void OnRender(DrawingContext dc)
    {
        var w = Math.Max(ActualWidth, ContentSeconds * Zoom);
        var h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var trackCount = Project?.Tracks.Count ?? 0;

        // ── lanes ──
        for (int t = 0; t < Math.Max(1, trackCount); t++)
        {
            var top = LaneTop(t);
            dc.DrawRectangle(t % 2 == 0 ? LaneA : LaneB, null, new Rect(0, top, w, LaneHeight));
            dc.DrawLine(LaneLine, new Point(0, top), new Point(w, top));
        }

        // ── grid ──
        var step = GridStep();
        for (double s = 0; s <= ContentSeconds; s += step)
        {
            var x = XOf(s);
            var major = Math.Abs(s / (step * 5) - Math.Round(s / (step * 5))) < 1e-6;
            dc.DrawLine(major ? GridPen : GridPenFaint, new Point(x, RulerHeight), new Point(x, h));
        }

        // ── ruler ──
        dc.DrawRectangle(RulerBg, null, new Rect(0, 0, w, RulerHeight));
        for (double s = 0; s <= ContentSeconds; s += step * 5)
        {
            var x = XOf(s);
            dc.DrawLine(GridPen, new Point(x, RulerHeight - 7), new Point(x, RulerHeight));
            var label = new FormattedText(Audio.Mixdown.Time(s), System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Face, 10.5, RulerText, 96);
            dc.DrawText(label, new Point(x + 4, 4));
        }

        // ── blocks ──
        if (Project != null)
        {
            for (int t = 0; t < Project.Tracks.Count; t++)
            {
                var track = Project.Tracks[t];
                var audible = Project.Audible(track);
                foreach (var item in track.Items) DrawItem(dc, item, t, audible);
            }
        }

        // ── snap guide while dragging ──
        if (_drag != DragMode.None && _snapGuide >= 0)
        {
            var x = XOf(_snapGuide);
            dc.DrawLine(SnapPen, new Point(x, RulerHeight), new Point(x, h));
        }

        // ── playhead ──
        var px = XOf(Playhead);
        dc.DrawLine(PlayPen, new Point(px, 0), new Point(px, h));
        var headBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
        headBrush.Freeze();
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(px - 5, 0), true, true);
            g.LineTo(new Point(px + 5, 0), true, false);
            g.LineTo(new Point(px, 8), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(headBrush, null, head);
    }

    private double GridStep()
    {
        // aim for a line roughly every 60 px, on a sensible round number
        double[] steps = { 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60 };
        foreach (var s in steps) if (s * Zoom >= 60) return s;
        return steps[^1];
    }

    private void DrawItem(DrawingContext dc, TimelineItem item, int lane, bool trackAudible)
    {
        var rect = RectOf(item, lane);
        if (rect.Right < 0 || rect.Width <= 0) return;

        var colour = ColorFor(item);
        var selected = ReferenceEquals(item, SelectedItem);

        var body = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        body.GradientStops.Add(new GradientStop(Color.FromArgb(selected ? (byte)0xDD : (byte)0xAA, colour.R, colour.G, colour.B), 0));
        body.GradientStops.Add(new GradientStop(Color.FromArgb(selected ? (byte)0x88 : (byte)0x55, colour.R, colour.G, colour.B), 1));
        body.Freeze();

        dc.DrawRoundedRectangle(body, selected ? SelectPen : null, rect, 6, 6);

        // waveform
        if (item.Peaks is { Length: > 0 } peaks && rect.Width > 6)
        {
            dc.PushClip(new RectangleGeometry(rect, 6, 6));
            var wave = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            wave.Freeze();
            var mid = rect.Top + rect.Height * 0.62;
            var maxH = rect.Height * 0.62;
            var columns = (int)Math.Min(rect.Width, 4000);
            for (int i = 0; i < columns; i++)
            {
                var p = peaks[Math.Clamp((int)((double)i / columns * peaks.Length), 0, peaks.Length - 1)];
                var bh = Math.Max(1, Math.Pow(p, 0.72) * maxH);
                dc.DrawRectangle(wave, null, new Rect(rect.X + i, mid - bh / 2, 1, bh));
            }
            dc.Pop();
        }

        // fades
        if (item.FadeIn > 0 || item.FadeOut > 0)
        {
            var fadeBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00));
            fadeBrush.Freeze();
            if (item.FadeIn > 0)
            {
                var fw = Math.Min(rect.Width, XOf(item.FadeIn));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(rect.X, rect.Top), true, true);
                    c.LineTo(new Point(rect.X + fw, rect.Top), true, false);
                    c.LineTo(new Point(rect.X, rect.Bottom), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(fadeBrush, null, g);
            }
            if (item.FadeOut > 0)
            {
                var fw = Math.Min(rect.Width, XOf(item.FadeOut));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(rect.Right, rect.Top), true, true);
                    c.LineTo(new Point(rect.Right - fw, rect.Top), true, false);
                    c.LineTo(new Point(rect.Right, rect.Bottom), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(fadeBrush, null, g);
            }
        }

        // label
        if (rect.Width > 34)
        {
            var suffix = item.Chain.ActiveCount > 0 ? $"  ·  {item.Chain.ActiveCount} fx" : "";
            var text = new FormattedText(item.Name + suffix, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Face, 11, TextBrush, 96)
            {
                MaxTextWidth = Math.Max(10, rect.Width - 10),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            dc.DrawText(text, new Point(rect.X + 6, rect.Y + 3));
        }

        if (item.Muted || !trackAudible)
            dc.DrawRoundedRectangle(MutedWash, null, rect, 6, 6);
    }

    // ══════════════════════════════════════════════════════════
    //  INTERACTION
    // ══════════════════════════════════════════════════════════
    private enum DragMode { None, Move, TrimStart, TrimEnd, Scrub }

    private DragMode _drag = DragMode.None;
    private TimelineItem _dragItem;
    private double _grabOffset;         // seconds between the pointer and the block's start
    private double _snapGuide = -1;
    private bool _changed;
    private const double EdgeGrab = 7;

    public TimelineItem HitTest(Point p, out int lane, out bool nearStart, out bool nearEnd)
    {
        lane = LaneAt(p.Y);
        nearStart = nearEnd = false;
        if (Project == null || Project.Tracks.Count == 0 || p.Y < RulerHeight) return null;

        var track = Project.Tracks[lane];
        for (int i = track.Items.Count - 1; i >= 0; i--)
        {
            var item = track.Items[i];
            var rect = RectOf(item, lane);
            if (!rect.Contains(p)) continue;
            nearStart = p.X - rect.Left <= EdgeGrab;
            nearEnd = rect.Right - p.X <= EdgeGrab;
            return item;
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);

        if (p.Y < RulerHeight)
        {
            _drag = DragMode.Scrub;
            SetPlayhead(SecondsAt(p.X));
            CaptureMouse();
            e.Handled = true;
            return;
        }

        var item = HitTest(p, out var lane, out var nearStart, out var nearEnd);
        Select(item);

        if (item == null)
        {
            _drag = DragMode.Scrub;
            SetPlayhead(SecondsAt(p.X));
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            ItemActivated?.Invoke(item);
            e.Handled = true;
            return;
        }

        _dragItem = item;
        _grabOffset = SecondsAt(p.X) - item.Start;
        _changed = false;
        _drag = nearStart ? DragMode.TrimStart : nearEnd ? DragMode.TrimEnd : DragMode.Move;
        EditStarting?.Invoke(_drag == DragMode.Move ? "Move block" : "Trim block");
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);

        if (_drag == DragMode.None)
        {
            var over = HitTest(p, out _, out var ns, out var ne);
            Cursor = p.Y < RulerHeight ? Cursors.Arrow
                   : over == null ? Cursors.Arrow
                   : (ns || ne) ? Cursors.SizeWE
                   : Cursors.SizeAll;
            return;
        }

        if (_drag == DragMode.Scrub) { SetPlayhead(SecondsAt(p.X)); return; }
        if (_dragItem == null) return;

        var seconds = SecondsAt(p.X);
        _snapGuide = -1;

        switch (_drag)
        {
            case DragMode.Move:
            {
                var target = Math.Max(0, seconds - _grabOffset);
                target = ApplySnap(target, _dragItem, isStart: true);
                if (Math.Abs(target - _dragItem.Start) > 1e-6) { _dragItem.Start = target; _changed = true; }

                // dropping onto another lane
                var lane = LaneAt(p.Y);
                var current = Project.Tracks.ToList().FindIndex(t => t.Items.Contains(_dragItem));
                if (lane != current && current >= 0)
                {
                    Project.Tracks[current].Items.Remove(_dragItem);
                    Project.Tracks[lane].Items.Add(_dragItem);
                    _changed = true;
                }
                break;
            }
            case DragMode.TrimStart:
            {
                var source = Project.Source(_dragItem.SourceId);
                if (source == null) break;
                var target = ApplySnap(Math.Max(0, seconds), _dragItem, isStart: true);
                var delta = target - _dragItem.Start;
                var newIn = Math.Clamp(_dragItem.TrimIn + delta, 0, Math.Max(0, _dragItem.TrimOut - 0.02));
                var actual = newIn - _dragItem.TrimIn;
                if (Math.Abs(actual) > 1e-6)
                {
                    _dragItem.TrimIn = newIn;
                    _dragItem.Start = Math.Max(0, _dragItem.Start + actual);
                    _changed = true;
                }
                break;
            }
            case DragMode.TrimEnd:
            {
                var source = Project.Source(_dragItem.SourceId);
                if (source == null) break;
                var target = ApplySnap(seconds, _dragItem, isStart: false);
                var wanted = target - _dragItem.Start;                 // desired length on the timeline
                // effects can stretch a block, so map back through the ratio we actually got
                var ratio = _dragItem.RawLength > 0 && _dragItem.Length > 0
                    ? _dragItem.RawLength / _dragItem.Length
                    : 1.0;
                var newOut = Math.Clamp(_dragItem.TrimIn + wanted * ratio,
                                        _dragItem.TrimIn + 0.02, source.Duration);
                if (Math.Abs(newOut - _dragItem.TrimOut) > 1e-6) { _dragItem.TrimOut = newOut; _changed = true; }
                break;
            }
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var wasEdit = _drag != DragMode.Scrub && _changed;
        _drag = DragMode.None;
        _dragItem = null;
        _snapGuide = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
        if (wasEdit) EditCommitted?.Invoke();
    }

    /// <summary>Pulls a dragged edge onto the playhead, zero, or another block's edge.</summary>
    private double ApplySnap(double seconds, TimelineItem moving, bool isStart)
    {
        if (!Snap || Project == null) return seconds;
        var tolerance = 9 / Math.Max(1, Zoom);
        var best = seconds;
        var bestDist = tolerance;

        void Consider(double candidate)
        {
            var d = Math.Abs(candidate - seconds);
            if (d < bestDist) { bestDist = d; best = candidate; _snapGuide = candidate; }
        }

        Consider(0);
        Consider(Playhead);
        foreach (var item in Project.AllItems)
        {
            if (ReferenceEquals(item, moving)) continue;
            Consider(item.Start);
            Consider(item.End);
        }

        // when moving a block, its far edge should snap too
        if (isStart && _drag == DragMode.Move)
        {
            var length = moving.Length;
            foreach (var item in Project.AllItems)
            {
                if (ReferenceEquals(item, moving)) continue;
                var d = Math.Abs(item.Start - length - seconds);
                if (d < bestDist) { bestDist = d; best = item.Start - length; _snapGuide = item.Start; }
            }
        }
        return Math.Max(0, best);
    }

    private void SetPlayhead(double seconds)
    {
        Playhead = Math.Max(0, seconds);
        PlayheadMoved?.Invoke(Playhead);
    }

    public void Select(TimelineItem item)
    {
        if (ReferenceEquals(item, SelectedItem)) return;
        if (SelectedItem != null) SelectedItem.Selected = false;
        SelectedItem = item;
        if (item != null) item.Selected = true;
        SelectionChanged?.Invoke(item);
        InvalidateVisual();
    }

    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }
}
