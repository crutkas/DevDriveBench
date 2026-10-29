using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Run;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDriveBench.Controls;

/// <summary>
/// One result row: name, the system-drive lane over the Dev Drive lane on a shared scale, and the
/// Dev Drive advantage. Pending rows recede; the active row is full strength.
/// </summary>
public sealed class ComparisonRow : Grid
{
    private readonly TestDescriptor _test;
    private readonly bool _compact;
    private readonly TextBlock _name;
    private readonly TextBlock _caption;
    private readonly LaneView _systemLane;
    private readonly LaneView _devLane;
    private readonly Grid _lanes;
    private readonly TextBlock _skip;
    private readonly TextBlock _advantage;
    private readonly TextBlock _status;
    private double _scale;
    private string _automationName = string.Empty;

    public ComparisonRow(TestDescriptor test, bool compact)
    {
        _test = test;
        _compact = compact;
        ColumnSpacing = 24;
        Padding = compact ? new Thickness(0, 7, 0, 7) : new Thickness(0, 12, 0, 12);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(350) };

        _name = new TextBlock
        {
            Text = test.Name,
            FontSize = compact ? 16 : 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _caption = new TextBlock
        {
            Text = compact ? test.Caption : test.Caption,
            FontSize = compact ? 12 : 13,
            Foreground = Theme.Tertiary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = compact ? 0 : 2 };
        nameStack.Children.Add(_name);
        nameStack.Children.Add(_caption);
        Children.Add(nameStack);

        double laneHeight = compact ? 12 : 22;
        _systemLane = CreateLane(laneHeight, Theme.SystemDrive, Theme.SystemDriveLive);
        _devLane = CreateLane(laneHeight, Theme.DevDrive, Theme.DevDriveLive);
        _lanes = new Grid { RowSpacing = compact ? 6 : 10, VerticalAlignment = VerticalAlignment.Center };
        _lanes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _lanes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _lanes.Children.Add(_systemLane);
        SetRow(_devLane, 1);
        _lanes.Children.Add(_devLane);
        SetColumn(_lanes, 1);
        Children.Add(_lanes);

        _skip = new TextBlock
        {
            FontSize = 14,
            Foreground = Theme.Tertiary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(34, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        SetColumn(_skip, 1);
        Children.Add(_skip);

        _advantage = new TextBlock
        {
            FontFamily = Theme.NumberFont,
            FontSize = compact ? 24 : 40,
            FontWeight = compact ? FontWeights.Normal : FontWeights.SemiLight,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Theme.Secondary,
        };
        _status = new TextBlock
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Theme.Tertiary,
        };
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_advantage);
        right.Children.Add(_status);
        SetColumn(right, 2);
        Children.Add(right);

        Opacity = 0.35;
    }

    public string TestId => _test.Id;

    public void Render(TestState t, RunInfo info, double now)
    {
        _systemLane.Letter = info.SystemDrive.Letter;
        _devLane.Letter = info.DevDrive.Letter;
        int iterations = Math.Max(1, info.Iterations);

        bool skipped = t.Status == TestStatus.Skipped;
        _lanes.Visibility = skipped ? Visibility.Collapsed : Visibility.Visible;
        _skip.Visibility = skipped ? Visibility.Visible : Visibility.Collapsed;
        _skip.Text = skipped ? "skipped · " + t.SkipReason : string.Empty;

        Opacity = t.Status switch
        {
            TestStatus.Pending => 0.32,
            TestStatus.Skipped => 0.55,
            _ => 1,
        };

        double? liveSystem = Live(t, DriveRole.System, now);
        double? liveDev = Live(t, DriveRole.Dev, now);

        double target = new[] { t.SystemValue, t.DevValue, liveSystem, liveDev }
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .Concat(iterations >= 2 ? t.SystemRuns.Concat(t.DevRuns) : Enumerable.Empty<double>())
            .DefaultIfEmpty(0)
            .Max();
        target = target <= 0 ? 1 : target * 1.02;

        // While streaming, the scale only grows (no jitter); once values settle it eases to fit.
        bool live = liveSystem is not null || liveDev is not null;
        _scale = live ? Math.Max(_scale, target) : target;

        _systemLane.Render(Lane(t, DriveRole.System, t.SystemValue, liveSystem, iterations));
        _devLane.Render(Lane(t, DriveRole.Dev, t.DevValue, liveDev, iterations));

        if (t.Advantage is double a && !skipped)
        {
            _advantage.Text = Fmt.Ratio(a);
            _advantage.Foreground = a >= 1.05 ? Theme.DevDrive : a <= 0.95 ? Theme.SystemDrive : Theme.Secondary;
        }
        else
        {
            _advantage.Text = skipped ? "—" : string.Empty;
            _advantage.Foreground = Theme.Tertiary;
        }

        _status.Text = Status(t, info, now, iterations);

        string name = skipped
            ? $"{_test.Name}: skipped, {t.SkipReason}"
            : $"{_test.Name}: {info.SystemDrive.Letter} {Text(t.SystemValue)}, {info.DevDrive.Letter} {Text(t.DevValue)}{(t.Advantage is double adv ? $", Dev Drive advantage {Fmt.Ratio(adv)}" : string.Empty)}";
        if (name != _automationName)
        {
            _automationName = name;
            AutomationProperties.SetName(this, name);
        }
    }

    private string Text(double? value) => value is double v ? Fmt.Value(v, _test.Unit) : "pending";

    private LaneSnapshot Lane(TestState t, DriveRole drive, double? value, double? live, int iterations)
    {
        string? hint = t.ActiveDrive == drive && t.ActivePhase == RunPhase.Setup ? "setup" : null;

        // Mid-series, name the in-flight run so its time isn't read as the summary bar's value.
        string? liveText = live is double l
            ? (iterations >= 2 ? $"run {t.ActiveIteration}  ·  {Fmt.Value(l, _test.Unit)}" : Fmt.Value(l, _test.Unit))
            : null;
        return new LaneSnapshot(
            value,
            live,
            value is double v ? Fmt.Value(v, _test.Unit) : null,
            liveText,
            t.RunsFor(drive),
            iterations >= 2,
            hint,
            _scale);
    }

    private double? Live(TestState t, DriveRole drive, double now)
    {
        if (t.ActiveDrive != drive || t.ActivePhase != RunPhase.Measure)
        {
            return null;
        }

        if (_test.Unit == "s")
        {
            return Math.Max(0, now - t.ActivePhaseStart);
        }

        // Disk passes: the bar tracks the instantaneous throughput.
        return t.LiveSamples.Count > 0 ? t.LiveSamples[^1].Value : 0;
    }

    private static string Status(TestState t, RunInfo info, double now, int iterations)
    {
        if (t.Status == TestStatus.Done)
        {
            return string.Empty;
        }

        if (t.Status != TestStatus.Running || t.ActivePhase is null)
        {
            return string.Empty;
        }

        string letter = t.ActiveDrive == DriveRole.Dev ? info.DevDrive.Letter : info.SystemDrive.Letter;
        string run = iterations >= 2 ? $" · run {t.ActiveIteration} of {iterations}" : string.Empty;
        return t.ActivePhase switch
        {
            RunPhase.Prepare => "preparing " + Fmt.Clock(now - t.ActivePhaseStart),
            RunPhase.Setup => $"setup {letter}{run}",
            RunPhase.Measure => $"timing {letter}{run}",
            RunPhase.Cleanup => "cleanup",
            _ => string.Empty,
        };
    }

    private static LaneView CreateLane(double height, Brush data, Brush live) => new()
    {
        LaneHeight = height,
        DataBrush = data,
        LiveBrush = live,
        LabelBrush = Theme.Primary,
        HintBrush = Theme.Tertiary,
        DiscardBrush = Theme.Tertiary,
    };
}
