using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DevDriveBench.Controls;

/// <summary>What one drive's lane should show this frame.</summary>
public readonly record struct LaneSnapshot(
    double? Value,
    double? Live,
    string? ValueText,
    string? LiveText,
    IReadOnlyList<double> Runs,
    bool ShowRuns,
    string? Hint,
    double Scale);

/// <summary>
/// One horizontal lane: a solid bar for the reported value, a dim bar with a bright leading edge for
/// the measurement in flight, thin ticks for every raw iteration (dim = discarded warm-up), and a
/// direct value label at the bar end. Geometry is driven through composition (Scale/Translation), so
/// per-frame updates never trigger layout.
/// </summary>
public sealed class LaneView : Grid
{
    private const double LabelReserve = 170;
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(450);

    private readonly TextBlock _letter = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas _track = new() { IsHitTestVisible = false };
    private readonly Rectangle _live = new();
    private readonly Rectangle _bar = new();
    private readonly Rectangle _edge = new() { Width = 2 };
    private readonly TextBlock _label = new();
    private readonly TextBlock _hint = new() { FontSize = 12 };
    private readonly List<Rectangle> _ticks = new();
    private readonly Vector3Transition _scaleTransition = new() { Duration = Settle };
    private readonly Vector3Transition _labelTransition = new() { Duration = Settle };
    private double _trackWidth;
    private bool _wasLive;

    public LaneView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Children.Add(_letter);
        SetColumn(_track, 1);
        Children.Add(_track);

        _track.Children.Add(_bar);
        _track.Children.Add(_live);
        _track.Children.Add(_edge);
        _track.Children.Add(_hint);
        _track.Children.Add(_label);

        _bar.Scale = new Vector3(0.0001f, 1, 1);
        _bar.ScaleTransition = _scaleTransition;
        _live.Scale = new Vector3(0.0001f, 1, 1);
        _live.Opacity = 0;
        _edge.Opacity = 0;

        _track.SizeChanged += (_, e) =>
        {
            _trackWidth = e.NewSize.Width;
            _bar.Width = MaxBarWidth;
            _live.Width = MaxBarWidth;
        };

        LaneHeight = 22;
    }

    /// <summary>Lane height in DIPs (bar thickness).</summary>
    public double LaneHeight
    {
        get => Height;
        set
        {
            Height = value;
            _bar.Height = value;
            _live.Height = value;
            _edge.Height = value;
            _label.FontSize = value >= 18 ? 16 : 13;
            _letter.FontSize = value >= 18 ? 16 : 13;
        }
    }

    public string Letter
    {
        get => _letter.Text;
        set => _letter.Text = value;
    }

    /// <summary>Data color for this drive.</summary>
    public Brush? DataBrush
    {
        get => _bar.Fill;
        set
        {
            _bar.Fill = value;
            _edge.Fill = value;
            _letter.Foreground = value;
        }
    }

    /// <summary>Dim fill for the measurement in flight.</summary>
    public Brush? LiveBrush
    {
        get => _live.Fill;
        set => _live.Fill = value;
    }

    /// <summary>Value label and iteration-tick color.</summary>
    public Brush? LabelBrush
    {
        get => _label.Foreground;
        set => _label.Foreground = value;
    }

    public Brush? HintBrush
    {
        get => _hint.Foreground;
        set => _hint.Foreground = value;
    }

    /// <summary>Tick color for the discarded warm-up iteration.</summary>
    public Brush? DiscardBrush { get; set; }

    private double MaxBarWidth => Math.Max(0, _trackWidth - LabelReserve);

    public void Render(in LaneSnapshot s)
    {
        double w = MaxBarWidth;
        double scale = s.Scale > 0 ? s.Scale : 1;
        double barFraction = s.Value is double v ? Math.Clamp(v / scale, 0, 1) : 0;
        bool isLive = s.Live is not null;
        double liveFraction = isLive ? Math.Clamp(s.Live!.Value / scale, 0, 1) : 0;

        _bar.Scale = new Vector3((float)Math.Max(barFraction, 0.0001), 1, 1);
        _live.Scale = new Vector3((float)Math.Max(liveFraction, 0.0001), 1, 1);

        // The in-flight run is drawn over the summary bar (like a progress fill over the previous result).
        _live.Opacity = isLive ? 0.85 : 0;
        _edge.Opacity = isLive ? 1 : 0;
        _edge.Translation = new Vector3((float)Math.Max(0, (liveFraction * w) - 2), 0, 0);

        // Labels sit past everything drawn in the lane (bar, live edge, every iteration tick).
        double ticksEnd = s.ShowRuns && s.Runs.Count > 0 ? Math.Clamp(s.Runs.Max() / scale, 0, 1) : 0;
        double end = Math.Max(Math.Max(barFraction, ticksEnd), isLive ? liveFraction : 0) * w;
        string text = (isLive ? s.LiveText : s.ValueText) ?? string.Empty;
        _label.Text = text;
        _label.TranslationTransition = isLive || _wasLive ? null : _labelTransition;
        _label.Translation = new Vector3((float)(end + (end > 0 ? 10 : 0)), (float)((Height - (_label.FontSize * 1.33)) / 2), 0);
        _wasLive = isLive;

        _hint.Text = s.Hint ?? string.Empty;
        double hintX = end + (end > 0 ? 10 : 0) + (text.Length > 0 ? (text.Length * _label.FontSize * 0.55) + 12 : 0);
        _hint.Translation = new Vector3((float)hintX, (float)((Height - 16) / 2), 0);

        RenderTicks(s, w, scale);
    }

    private void RenderTicks(in LaneSnapshot s, double w, double scale)
    {
        int count = s.ShowRuns ? s.Runs.Count : 0;
        while (_ticks.Count < count)
        {
            var tick = new Rectangle { Width = 2 };
            _ticks.Add(tick);
            _track.Children.Add(tick);
        }

        for (int i = 0; i < _ticks.Count; i++)
        {
            Rectangle tick = _ticks[i];
            if (i >= count)
            {
                tick.Opacity = 0;
                continue;
            }

            bool discarded = count >= 2 && i == 0;
            double x = Math.Clamp(s.Runs[i] / scale, 0, 1) * w;
            tick.Height = Height + 8;
            tick.Fill = discarded ? DiscardBrush ?? _label.Foreground : _label.Foreground;
            tick.Opacity = discarded ? 0.8 : 0.9;
            tick.Translation = new Vector3((float)Math.Max(0, x - 1), -4, 0);
        }
    }
}
