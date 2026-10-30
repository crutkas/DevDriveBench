using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Run;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDriveBench.Controls;

/// <summary>
/// One result row: name, the system-drive lane over the Dev Drive lane(s) on a shared scale, and the
/// Dev Drive advantage. Workloads with a package cache can show two Dev Drive lanes, in scenario order:
/// code on the Dev Drive with packages on the system drive, then code and packages on the Dev Drive —
/// one from this run, the other from the latest run with the packages on the other drive.
/// Pending rows recede; the active row is full strength.
/// </summary>
public sealed class ComparisonRow : Grid
{
    private readonly TestDescriptor _test;
    private readonly bool _compact;
    private readonly string? _packageVariable;
    private readonly TextBlock _name;
    private readonly TextBlock _caption;
    private readonly LaneView _systemLane;
    private readonly LaneView _mixedLane;
    private readonly LaneView _devLane;
    private readonly StackPanel _lanes;
    private readonly TextBlock _skip;
    private readonly TextBlock _advantage;
    private readonly TextBlock _status;
    private double _scale;
    private string _automationName = string.Empty;

    public ComparisonRow(TestDescriptor test, bool compact)
    {
        _test = test;
        _compact = compact;

        // Recordings from before package locations lack the variable; the workload still had one.
        _packageVariable = test.PackageVariable ?? (test.IsWorkload ? TestCatalog.PackageVariable(test.Group) : null);
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
        _mixedLane = CreateLane(laneHeight, Theme.DevDriveMixed, Theme.DevDriveLive);
        _devLane = CreateLane(laneHeight, Theme.DevDrive, Theme.DevDriveLive);
        _mixedLane.Visibility = Visibility.Collapsed;
        _lanes = new StackPanel { Spacing = compact ? 6 : 10, VerticalAlignment = VerticalAlignment.Center };
        _lanes.Children.Add(_systemLane);
        _lanes.Children.Add(_mixedLane);
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

    public void Render(TestState t, RunInfo info, double now, TestResult? other = null)
    {
        _systemLane.Letter = info.SystemDrive.Letter;
        _mixedLane.Letter = info.DevDrive.Letter;
        _devLane.Letter = info.DevDrive.Letter;
        int iterations = Math.Max(1, info.Iterations);
        string caption = TestCatalog.EmulationNote(_test, info.Machine) is string note ? $"{_test.Caption}  ·  {note}" : _test.Caption;
        if (_caption.Text != caption)
        {
            _caption.Text = caption;
        }

        bool skipped = t.Status == TestStatus.Skipped;
        _lanes.Visibility = skipped ? Visibility.Collapsed : Visibility.Visible;
        _skip.Visibility = skipped ? Visibility.Visible : Visibility.Collapsed;
        _skip.Text = skipped ? "skipped · " + t.SkipReason : string.Empty;

        Opacity = t.Status switch
        {
            TestStatus.Pending => 0.32,
            TestStatus.Ready => 0.7,
            TestStatus.Skipped => 0.55,
            _ => 1,
        };

        // This run's Dev Drive result goes in the lane for its package location; the latest run with the
        // packages on the other drive (if any) fills the other lane, so both scenarios read top to bottom.
        bool packagesOnSystem = _packageVariable is not null && info.Packages == PackageLocation.SystemDrive;
        LaneView current = packagesOnSystem ? _mixedLane : _devLane;
        LaneView reference = packagesOnSystem ? _devLane : _mixedLane;
        TestResult? cmp = _packageVariable is not null && other is { DevValue: not null, Status: not TestStatus.Skipped } ? other : null;
        current.Visibility = Visibility.Visible;
        reference.Visibility = cmp is null ? Visibility.Collapsed : Visibility.Visible;

        double? liveSystem = Live(t, DriveRole.System, now);
        double? liveDev = Live(t, DriveRole.Dev, now);

        double target = new[] { t.SystemValue, t.DevValue, liveSystem, liveDev, cmp?.DevValue }
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .Concat(iterations >= 2 ? t.SystemRuns.Concat(t.DevRuns) : Enumerable.Empty<double>())
            .Concat(cmp is { DevRuns.Count: >= 2 } ? cmp.DevRuns : Enumerable.Empty<double>())
            .DefaultIfEmpty(0)
            .Max();
        target = target <= 0 ? 1 : target * 1.02;

        // While streaming, the scale only grows (no jitter); once values settle it eases to fit.
        bool live = liveSystem is not null || liveDev is not null;
        _scale = live ? Math.Max(_scale, target) : target;

        _systemLane.Render(Lane(t, DriveRole.System, t.SystemValue, liveSystem, iterations, hint: null));
        current.Render(Lane(t, DriveRole.Dev, t.DevValue, liveDev, iterations, PackagesHint(info, packagesOnSystem)));
        if (cmp is not null)
        {
            reference.Render(new LaneSnapshot(
                cmp.DevValue,
                null,
                Fmt.Value(cmp.DevValue!.Value, _test.Unit),
                null,
                cmp.DevRuns,
                cmp.DevRuns.Count >= 2,
                PackagesHint(info, !packagesOnSystem),
                _scale));
        }

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

        string status = Status(t, info, now, iterations);
        string? otherAdvantage = cmp?.Advantage is double oa
            ? $"{Fmt.Ratio(oa)}  ·  packages: {TestCatalog.PackagesMode(packagesOnSystem ? PackageLocation.DevDrive : PackageLocation.SystemDrive, info)}"
            : null;
        _status.Text = status.Length == 0 && !skipped && otherAdvantage is not null ? otherAdvantage : status;
        _status.Foreground = t.Status == TestStatus.Ready ? Theme.DevDrive : status.Length == 0 && otherAdvantage is not null ? Theme.Secondary : Theme.Tertiary;

        string packagesNote = _packageVariable is null ? string.Empty : $" with packages on {(packagesOnSystem ? info.SystemDrive.Letter : info.DevDrive.Letter)}";
        string name = skipped
            ? $"{_test.Name}: skipped, {t.SkipReason}"
            : $"{_test.Name}: {info.SystemDrive.Letter} {Text(t.SystemValue)}, {info.DevDrive.Letter}{packagesNote} {Text(t.DevValue)}{(t.Advantage is double adv ? $", Dev Drive advantage {Fmt.Ratio(adv)}" : string.Empty)}" +
              (cmp is null ? string.Empty : $"; earlier run with packages on {(packagesOnSystem ? info.DevDrive.Letter : info.SystemDrive.Letter)}: {info.DevDrive.Letter} {Text(cmp.DevValue)}{(cmp.Advantage is double ca ? $", {Fmt.Ratio(ca)}" : string.Empty)}");
        if (name != _automationName)
        {
            _automationName = name;
            AutomationProperties.SetName(this, name);
        }
    }

    /// <summary>Names the variable that placed this lane's packages, e.g. "NUGET_PACKAGES on C:".</summary>
    private string? PackagesHint(RunInfo info, bool packagesOnSystem) =>
        _packageVariable is null ? null : $"{_packageVariable} on {(packagesOnSystem ? info.SystemDrive.Letter : info.DevDrive.Letter)}";

    private string Text(double? value) => value is double v ? Fmt.Value(v, _test.Unit) : "pending";

    private LaneSnapshot Lane(TestState t, DriveRole drive, double? value, double? live, int iterations, string? hint)
    {
        if (t.ActiveDrive == drive && t.ActivePhase == RunPhase.Setup)
        {
            hint = "setup";
        }

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

        if (t.Status == TestStatus.Ready)
        {
            return t.PrepareSeconds is double s && s >= 0.5 ? $"ready · {Fmt.Clock(s)}" : "ready";
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
