using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MonRem.Client.Core.Panels;
using MonRem.Protocol.Panels;

namespace MonRem.App.Views;

/// <summary>
/// Desenha um painel e traduz toques em ações. A área lógica do painel é escalada para caber no
/// controle mantendo a proporção. Multitoque: cada dedo tem seu próprio gesto.
/// </summary>
public sealed class PanelCanvas : Control
{
    private const double DetentPixels = 24;   // arrasto vertical (lógico) por detente do encoder
    private const double TapSlop = 10;        // movimento máximo para ainda ser um toque

    private static readonly IBrush PanelBrush = Solid("#15181C");
    private static readonly IBrush WidgetBrush = Solid("#2A2F36");
    private static readonly IBrush WidgetPressedBrush = Solid("#3A414A");
    private static readonly IBrush TextBrush = Solid("#E6E9ED");
    private static readonly IBrush DimTextBrush = Solid("#8A939E");
    private static readonly IBrush LitBrush = Solid("#2ECC71");
    private static readonly IBrush DisplayBrush = Solid("#050607");
    private static readonly IBrush DisplayTextBrush = Solid("#F5A623");
    private static readonly IPen BorderPen = new Pen(Solid("#454C55"), 1.5);
    private static readonly IPen InvalidPen = new Pen(Solid("#E74C3C"), 2, new DashStyle([4, 3], 0));
    private static readonly IPen NeedlePen = new Pen(Solid("#F5A623"), 3);
    private static readonly IPen ArcPen = new Pen(Solid("#454C55"), 6);
    private static readonly IBrush AmberBrush = Solid("#F5A623");
    private static readonly IPen ThickTextPen = new Pen(Solid("#E6E9ED"), 3);
    private static readonly Typeface Face = new(FontFamily.Default);

    private readonly PanelRuntime _runtime;
    private readonly Dictionary<int, Gesture> _gestures = [];
    private readonly Dictionary<WidgetState, int> _encoderTurns = [];
    private int _redrawQueued;

    public PanelCanvas(PanelRuntime runtime)
    {
        _runtime = runtime;
        _runtime.ValueChanged += _ => QueueRedraw();
        ClipToBounds = true;
        Focusable = true;
    }

    // ------------------------------------------------------------------ desenho

    public override void Render(DrawingContext context)
    {
        var panel = _runtime.Panel;
        context.DrawRectangle(Brush(panel.Background) ?? PanelBrush, null, new Rect(Bounds.Size));

        var (scale, offset) = Fit();
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y));

        foreach (var w in _runtime.Widgets) DrawWidget(context, w);
    }

    private void DrawWidget(DrawingContext ctx, WidgetState w)
    {
        var m = w.Model;
        var r = new Rect(m.X, m.Y, m.W, m.H);
        var pressed = _gestures.Values.Any(g => g.Widget == w);
        var accent = Brush(m.Color) ?? LitBrush;

        switch (m.Type)
        {
            case WidgetTypes.Button:
            {
                var lit = _runtime.IsLit(w);
                ctx.DrawRectangle(pressed ? WidgetPressedBrush : WidgetBrush, BorderPen, r, 8, 8);
                if (lit) ctx.DrawRectangle(accent, null, new Rect(r.X + 10, r.Y + 6, r.Width - 20, 5), 2, 2);
                Text(ctx, m.Label ?? "", r, Size(m, r.Height * 0.3), lit ? accent : TextBrush);
                break;
            }
            case WidgetTypes.Toggle:
            {
                var on = _runtime.IsLit(w);
                ctx.DrawRectangle(WidgetBrush, BorderPen, r, 8, 8);
                var knobArea = new Rect(r.X + 8, r.Bottom - (r.Height * 0.45) - 6, r.Width - 16, r.Height * 0.45);
                ctx.DrawRectangle(on ? accent : WidgetPressedBrush, null, knobArea, knobArea.Height / 2, knobArea.Height / 2);
                var d = knobArea.Height - 6;
                var knobX = on ? knobArea.Right - d - 3 : knobArea.X + 3;
                ctx.DrawEllipse(TextBrush, null, new Rect(knobX, knobArea.Y + 3, d, d));
                Text(ctx, m.Label ?? "", new Rect(r.X, r.Y, r.Width, r.Height * 0.5), Size(m, r.Height * 0.22), TextBrush);
                break;
            }
            case WidgetTypes.Encoder:
            {
                var size = Math.Min(r.Width, r.Height);
                var knob = new Rect(r.Center.X - (size / 2) + 4, r.Center.Y - (size / 2) + 4, size - 8, size - 8);
                ctx.DrawEllipse(pressed ? WidgetPressedBrush : WidgetBrush, BorderPen, knob);
                var turns = _encoderTurns.GetValueOrDefault(w);
                var angle = (turns * 15 - 90) * Math.PI / 180;
                var rad = knob.Width / 2;
                ctx.DrawLine(ThickTextPen,
                    new Point(knob.Center.X + (Math.Cos(angle) * rad * 0.45), knob.Center.Y + (Math.Sin(angle) * rad * 0.45)),
                    new Point(knob.Center.X + (Math.Cos(angle) * rad * 0.9), knob.Center.Y + (Math.Sin(angle) * rad * 0.9)));
                Text(ctx, m.Label ?? "", knob, Size(m, knob.Height * 0.16), DimTextBrush);
                break;
            }
            case WidgetTypes.Selector:
            {
                ctx.DrawRectangle(WidgetBrush, BorderPen, r, 8, 8);
                var labelHeight = string.IsNullOrEmpty(m.Label) ? 0 : r.Height * 0.3;
                if (labelHeight > 0) Text(ctx, m.Label!, new Rect(r.X, r.Y, r.Width, labelHeight), Size(m, labelHeight * 0.6), DimTextBrush);
                var current = _runtime.SelectorIndex(w);
                for (var i = 0; i < m.Positions.Count; i++)
                {
                    var seg = Segment(w, i, labelHeight);
                    if (current == i) ctx.DrawRectangle(accent, null, seg.Deflate(3), 6, 6);
                    Text(ctx, m.Positions[i].Label, seg, Size(m, seg.Height * 0.4), current == i ? Brushes.Black : TextBrush);
                }
                break;
            }
            case WidgetTypes.Display:
                ctx.DrawRectangle(DisplayBrush, BorderPen, r, 4, 4);
                Text(ctx, _runtime.DisplayText(w), r, Size(m, r.Height * 0.6), Brush(m.Color) ?? DisplayTextBrush);
                break;
            case WidgetTypes.Gauge:
                DrawGauge(ctx, w, r);
                break;
            case WidgetTypes.Annunciator:
            {
                var lit = _runtime.IsLit(w);
                ctx.DrawRectangle(lit ? Brush(m.Color) ?? AmberBrush : WidgetBrush, BorderPen, r, 4, 4);
                Text(ctx, m.Label ?? "", r, Size(m, r.Height * 0.35), lit ? Brushes.Black : DimTextBrush);
                break;
            }
            case WidgetTypes.Label:
                Text(ctx, m.Label ?? "", r, Size(m, r.Height * 0.5), Brush(m.Color) ?? TextBrush);
                break;
        }

        if (!w.IsValid) ctx.DrawRectangle(null, InvalidPen, r);
    }

    private void DrawGauge(DrawingContext ctx, WidgetState w, Rect r)
    {
        var m = w.Model;
        var size = Math.Min(r.Width, r.Height);
        var center = new Point(r.Center.X, r.Y + (size * 0.55));
        var radius = size * 0.42;
        const double start = 150, sweep = 240; // graus, sentido horário a partir do eixo X

        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(Polar(center, radius, start), false);
            g.ArcTo(Polar(center, radius, start + sweep), new Avalonia.Size(radius, radius), 0, true, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, ArcPen, arc);

        if (_runtime.GaugeFraction(w) is { } f)
            ctx.DrawLine(NeedlePen, center, Polar(center, radius * 0.9, start + (sweep * f)));
        ctx.DrawEllipse(TextBrush, null, new Rect(center.X - 5, center.Y - 5, 10, 10));

        var value = _runtime.Number(m.Source) is { } n
            ? ValueFormat.Format(m.Format ?? "0", n, null) + (m.Unit is null ? "" : $" {m.Unit}")
            : "---";
        Text(ctx, value, new Rect(r.X, center.Y + (radius * 0.25), r.Width, radius * 0.4), Size(m, radius * 0.25), TextBrush);
        Text(ctx, m.Label ?? "", new Rect(r.X, center.Y - (radius * 0.55), r.Width, radius * 0.3), radius * 0.18, DimTextBrush);
    }

    // ------------------------------------------------------------------ toque, mouse e roda

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = ToLogical(e.GetPosition(this));
        var w = HitTestWidget(p);
        if (w is null || !w.IsValid) return;

        e.Pointer.Capture(this);
        e.Handled = true;
        var gesture = new Gesture(w, p);
        _gestures[e.Pointer.Id] = gesture;

        if (w.Model.Type == WidgetTypes.Button) Fire(_runtime.PressAsync(w));
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_gestures.TryGetValue(e.Pointer.Id, out var g)) return;
        var p = ToLogical(e.GetPosition(this));
        if (Distance(p, g.Start) > TapSlop) g.Moved = true;
        if (g.Widget.Model.Type != WidgetTypes.Encoder) return;

        // Arrastar para cima = incrementar. Cada DetentPixels vira um detente.
        var detents = (int)((g.Start.Y - p.Y) / DetentPixels) - g.DetentsSent;
        if (detents == 0) return;
        g.DetentsSent += detents;
        Turn(g.Widget, detents);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_gestures.Remove(e.Pointer.Id, out var g)) return;
        var w = g.Widget;
        var p = ToLogical(e.GetPosition(this));

        switch (w.Model.Type)
        {
            case WidgetTypes.Button:
                Fire(_runtime.ReleaseAsync(w));
                break;
            case WidgetTypes.Toggle when !g.Moved:
                Fire(_runtime.SetToggleAsync(w, !_runtime.IsLit(w)));
                break;
            case WidgetTypes.Encoder when !g.Moved && g.DetentsSent == 0:
                Fire(_runtime.PushAsync(w));
                break;
            case WidgetTypes.Selector when !g.Moved:
                var index = SegmentAt(w, p);
                if (index >= 0) Fire(_runtime.SelectAsync(w, index));
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_gestures.Remove(e.Pointer.Id, out var g) && g.Widget.Model.Type == WidgetTypes.Button)
            Fire(_runtime.ReleaseAsync(g.Widget));
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var w = HitTestWidget(ToLogical(e.GetPosition(this)));
        if (w is null || !w.IsValid || w.Model.Type != WidgetTypes.Encoder) return;
        var detents = Math.Sign(e.Delta.Y);
        if (detents == 0) return;
        Turn(w, detents);
        e.Handled = true;
    }

    private void Turn(WidgetState w, int detents)
    {
        _encoderTurns[w] = _encoderTurns.GetValueOrDefault(w) + detents;
        Fire(_runtime.RotateAsync(w, detents));
        InvalidateVisual();
    }

    private static void Fire(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    // ------------------------------------------------------------------ geometria

    private (double Scale, Point Offset) Fit()
    {
        var panel = _runtime.Panel;
        var scale = Math.Min(Bounds.Width / panel.Width, Bounds.Height / panel.Height);
        if (scale <= 0 || double.IsNaN(scale)) scale = 1;
        return (scale, new Point((Bounds.Width - (panel.Width * scale)) / 2, (Bounds.Height - (panel.Height * scale)) / 2));
    }

    private Point ToLogical(Point p)
    {
        var (scale, offset) = Fit();
        return new Point((p.X - offset.X) / scale, (p.Y - offset.Y) / scale);
    }

    private WidgetState? HitTestWidget(Point p) =>
        _runtime.Widgets.LastOrDefault(w => w.Model.Type != WidgetTypes.Label && new Rect(w.Model.X, w.Model.Y, w.Model.W, w.Model.H).Contains(p));

    private static Rect Segment(WidgetState w, int i, double labelHeight)
    {
        var m = w.Model;
        var width = m.W / Math.Max(1, m.Positions.Count);
        return new Rect(m.X + (i * width), m.Y + labelHeight, width, m.H - labelHeight);
    }

    private static int SegmentAt(WidgetState w, Point p)
    {
        if (w.Model.Positions.Count == 0) return -1;
        var i = (int)((p.X - w.Model.X) / (w.Model.W / w.Model.Positions.Count));
        return Math.Clamp(i, 0, w.Model.Positions.Count - 1);
    }

    private static Point Polar(Point c, double radius, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(c.X + (radius * Math.Cos(a)), c.Y + (radius * Math.Sin(a)));
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static double Size(WidgetModel m, double fallback) => m.FontSize ?? Math.Max(8, fallback);

    private static void Text(DrawingContext ctx, string text, Rect area, double size, IBrush brush)
    {
        if (text.Length == 0) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush)
        {
            MaxTextWidth = Math.Max(1, area.Width - 6),
            TextAlignment = TextAlignment.Center,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        ctx.DrawText(ft, new Point(area.X + 3, area.Y + ((area.Height - ft.Height) / 2)));
    }

    private static IBrush Solid(string color) => new SolidColorBrush(Color.Parse(color));

    private static IBrush? Brush(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        return Color.TryParse(color, out var c) ? new SolidColorBrush(c) : null;
    }

    private void QueueRedraw()
    {
        // Valores chegam pela rede em outra thread: no máximo um redesenho pendente por vez.
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _redrawQueued, 0);
            InvalidateVisual();
        });
    }

    private sealed class Gesture(WidgetState widget, Point start)
    {
        public WidgetState Widget { get; } = widget;
        public Point Start { get; } = start;
        public bool Moved { get; set; }
        public int DetentsSent { get; set; }
    }
}
