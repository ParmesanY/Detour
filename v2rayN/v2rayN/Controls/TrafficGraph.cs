using System.Windows.Media;

namespace v2rayN.Controls;

/// <summary>
/// Small live traffic graph: download as a filled area, upload as a thin line, over a faint grid.
/// Values are bytes/s, oldest first. The scale follows the peak of the visible window.
/// </summary>
public class TrafficGraph : FrameworkElement
{
    public static readonly DependencyProperty DownProperty = Register(nameof(Down), typeof(double[]));
    public static readonly DependencyProperty UpProperty = Register(nameof(Up), typeof(double[]));
    public static readonly DependencyProperty DownBrushProperty = Register(nameof(DownBrush), typeof(Brush));
    public static readonly DependencyProperty DownFillProperty = Register(nameof(DownFill), typeof(Brush));
    public static readonly DependencyProperty UpBrushProperty = Register(nameof(UpBrush), typeof(Brush));
    public static readonly DependencyProperty GridBrushProperty = Register(nameof(GridBrush), typeof(Brush));

    public double[]? Down { get => (double[]?)GetValue(DownProperty); set => SetValue(DownProperty, value); }
    public double[]? Up { get => (double[]?)GetValue(UpProperty); set => SetValue(UpProperty, value); }
    public Brush? DownBrush { get => (Brush?)GetValue(DownBrushProperty); set => SetValue(DownBrushProperty, value); }
    public Brush? DownFill { get => (Brush?)GetValue(DownFillProperty); set => SetValue(DownFillProperty, value); }
    public Brush? UpBrush { get => (Brush?)GetValue(UpBrushProperty); set => SetValue(UpBrushProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    /// <summary>Below this the graph does not zoom in further, so idle noise stays flat.</summary>
    private const double MinScale = 64 * 1024;

    private static DependencyProperty Register(string name, Type type) =>
        DependencyProperty.Register(name, type, typeof(TrafficGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 4)
        {
            return;
        }

        // Transparent hit area + grid: 4 rows, vertical ticks every 15 samples.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (GridBrush is { } gb)
        {
            var pen = new Pen(gb, 1);
            pen.Freeze();
            for (var i = 1; i < 4; i++)
            {
                var y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
            var n = Math.Max(Down?.Length ?? 0, 2);
            for (var i = 15; i < n; i += 15)
            {
                var x = Math.Round(w * i / (n - 1)) + 0.5;
                dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
            }
        }

        var max = MinScale;
        foreach (var v in (Down ?? []).Concat(Up ?? []))
        {
            max = Math.Max(max, v);
        }
        max *= 1.15;

        if (Down is { Length: > 1 } down)
        {
            var area = Build(down, w, h, max, closed: true);
            dc.DrawGeometry(DownFill, null, area);
            var line = Build(down, w, h, max, closed: false);
            dc.DrawGeometry(null, Frozen(new Pen(DownBrush ?? Brushes.SteelBlue, 1.5) { LineJoin = PenLineJoin.Round }), line);
        }
        if (Up is { Length: > 1 } up)
        {
            var line = Build(up, w, h, max, closed: false);
            dc.DrawGeometry(null, Frozen(new Pen(UpBrush ?? Brushes.Gray, 1) { LineJoin = PenLineJoin.Round }), line);
        }
    }

    private static Pen Frozen(Pen p)
    {
        p.Freeze();
        return p;
    }

    private static StreamGeometry Build(double[] values, double w, double h, double max, bool closed)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var step = w / (values.Length - 1);
            Point P(int i) => new(i * step, h - 1 - (h - 2) * Math.Min(1, values[i] / max));

            if (closed)
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                ctx.LineTo(P(0), false, false);
            }
            else
            {
                ctx.BeginFigure(P(0), false, false);
            }
            for (var i = 1; i < values.Length; i++)
            {
                ctx.LineTo(P(i), true, true);
            }
            if (closed)
            {
                ctx.LineTo(new Point(w, h), false, false);
            }
        }
        g.Freeze();
        return g;
    }
}
