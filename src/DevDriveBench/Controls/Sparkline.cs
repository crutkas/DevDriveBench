using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DevDriveBench.Controls;

/// <summary>A sample for <see cref="Sparkline"/>: time (s), value, and which series color to draw it in.</summary>
public readonly record struct SparkPoint(double T, double Value, int Series);

/// <summary>
/// Tufte sparkline over a sliding time window: a bare line (colored per segment), a baseline hairline,
/// and the current value labeled directly at the line's end. No axes, no grid.
/// </summary>
public sealed class Sparkline : Grid
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Rectangle _baseline = new() { Height = 1 };
    private readonly TextBlock _maxLabel = new() { FontSize = 11 };
    private readonly List<Polyline> _lines = new();
    private double _width;
    private double _height;
    private double _displayMax;

    public Sparkline()
    {
        Children.Add(_canvas);
        _canvas.Children.Add(_baseline);
        _canvas.Children.Add(_maxLabel);
        _canvas.SizeChanged += (_, e) =>
        {
            _width = e.NewSize.Width;
            _height = e.NewSize.Height;
            _baseline.Width = _width;
            Canvas.SetTop(_baseline, _height - 1);
        };
    }

    /// <summary>Brush per series index.</summary>
    public IReadOnlyList<Brush> SeriesBrushes { get; set; } = Array.Empty<Brush>();

    public Brush? BaselineBrush
    {
        get => _baseline.Fill;
        set => _baseline.Fill = value;
    }

    public Brush? LabelBrush
    {
        get => _maxLabel.Foreground;
        set => _maxLabel.Foreground = value;
    }

    /// <summary>Smallest top of scale, so near-idle noise stays flat instead of filling the chart.</summary>
    public double MinimumMax { get; set; } = 1;

    /// <summary>Fixed top of scale (e.g. 100 for CPU %), or null to auto-scale to the visible window.</summary>
    public double? FixedMax { get; set; }

    public double StrokeThickness { get; set; } = 1.5;

    public Func<double, string>? FormatMax { get; set; }

    public void Render(IReadOnlyList<SparkPoint> points, double now, double window)
    {
        if (_width <= 0 || _height <= 0)
        {
            return;
        }

        double start = now - window;
        int first = 0;
        while (first < points.Count - 1 && points[first + 1].T < start)
        {
            first++;
        }

        double max = FixedMax ?? Math.Max(MinimumMax, VisibleMax(points, first) * 1.15);
        if (FixedMax is null)
        {
            // Ease the auto-scale so the trace doesn't jump when a spike enters or leaves.
            _displayMax = _displayMax <= 0 ? max : _displayMax + ((max - _displayMax) * 0.08);
            max = Math.Max(_displayMax, MinimumMax);
        }

        int used = 0;
        Polyline? line = null;
        int series = -1;
        Point? previous = null;
        for (int i = first; i < points.Count; i++)
        {
            SparkPoint p = points[i];
            var pt = new Point(Math.Clamp((p.T - start) / window, 0, 1) * _width, _height - 1 - (Math.Clamp(p.Value / max, 0, 1) * (_height - 2)));
            if (line is null || p.Series != series)
            {
                line = Line(used++, p.Series);
                series = p.Series;
                if (previous is Point prev)
                {
                    line.Points.Add(prev);
                }
            }

            line.Points.Add(pt);
            previous = pt;
        }

        for (int i = used; i < _lines.Count; i++)
        {
            _lines[i].Points.Clear();
        }

        _maxLabel.Text = FixedMax is null && points.Count > 0 && FormatMax is not null ? FormatMax(max) : string.Empty;
        Canvas.SetTop(_maxLabel, -2);
    }

    /// <summary>Clears the trace.</summary>
    public void Reset()
    {
        _displayMax = 0;
        foreach (Polyline l in _lines)
        {
            l.Points.Clear();
        }

        _maxLabel.Text = string.Empty;
    }

    private static double VisibleMax(IReadOnlyList<SparkPoint> points, int first)
    {
        double max = 0;
        for (int i = first; i < points.Count; i++)
        {
            max = Math.Max(max, points[i].Value);
        }

        return max;
    }

    private Polyline Line(int index, int series)
    {
        while (_lines.Count <= index)
        {
            var pl = new Polyline { StrokeLineJoin = PenLineJoin.Round, StrokeThickness = StrokeThickness };
            _lines.Add(pl);
            _canvas.Children.Add(pl);
        }

        Polyline line = _lines[index];
        line.Points.Clear();
        line.StrokeThickness = StrokeThickness;
        line.Stroke = series >= 0 && series < SeriesBrushes.Count ? SeriesBrushes[series] : SeriesBrushes.FirstOrDefault();
        return line;
    }
}
