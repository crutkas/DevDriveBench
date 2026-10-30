using DevDriveBench.Controls;
using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Run;
using DevDriveBench.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DevDriveBench;

/// <summary>
/// The stage. Everything visual is derived each frame from <see cref="MainViewModel.State"/> (the
/// folded event stream), so Live and Replay render through exactly the same code.
/// </summary>
public sealed partial class MainPage : Page
{
    private const double ActivityWindowSeconds = 60;

    private readonly Dictionary<string, ComparisonRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TestResult> _comparison = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SparkPoint> _io = new();
    private readonly List<SparkPoint> _cpu = new();
    private double _headlineShown;
    private int _dirtyFrames = 60;
    private long _frame;
    private bool _syncing;

    public MainPage()
    {
        ViewModel = new MainViewModel(DispatcherQueue.GetForCurrentThread());
        InitializeComponent();

        IoSpark.SeriesBrushes = new[] { Theme.Tertiary, Theme.SystemDrive, Theme.DevDrive };
        IoSpark.BaselineBrush = Theme.Hairline;
        IoSpark.LabelBrush = Theme.Tertiary;
        IoSpark.MinimumMax = 50;
        IoSpark.StrokeThickness = 2;
        IoSpark.FormatMax = Fmt.Throughput;

        CpuSpark.SeriesBrushes = new[] { Theme.Secondary };
        CpuSpark.BaselineBrush = Theme.Hairline;
        CpuSpark.FixedMax = 100;
        CpuSpark.StrokeThickness = 1.25;

        ViewModel.StateReset += (_, _) => RebuildRows();
        ViewModel.EventApplied += OnEventApplied;
        ViewModel.PropertyChanged += (_, e) => SyncSelectors(e.PropertyName);
        SizeChanged += (_, _) => _dirtyFrames = 30;
        Loaded += OnLoaded;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public MainViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering += OnFrame;
        NowText.Text = "Probing drives and tools";
        await ViewModel.InitializeAsync();
        SyncSelectors(null);
        _dirtyFrames = 60;
    }

    private void OnEventApplied(object? sender, BenchEvent e)
    {
        _dirtyFrames = 30;
        if (e is RunStartedEvent)
        {
            RebuildRows();
            return;
        }

        if (e is not MachineActivityEvent a)
        {
            return;
        }

        // Plot the I/O of the drive being exercised right now; prep/seed work happens on the system drive.
        PhaseStartedEvent? open = ViewModel.State.OpenPhase;
        DriveRole role = open?.Drive ?? DriveRole.None;
        int series = role switch { DriveRole.Dev => 2, DriveRole.System => 1, _ => 0 };
        _io.Add(new SparkPoint(a.T, role == DriveRole.Dev ? a.DevMBps : a.SystemMBps, series));
        _cpu.Add(new SparkPoint(a.T, a.CpuPercent, 0));
        Trim(_io, a.T);
        Trim(_cpu, a.T);
    }

    private static void Trim(List<SparkPoint> points, double now)
    {
        int stale = 0;
        while (stale < points.Count - 2 && points[stale + 1].T < now - ActivityWindowSeconds - 2)
        {
            stale++;
        }

        if (stale > 0)
        {
            points.RemoveRange(0, stale);
        }
    }

    private void RebuildRows()
    {
        WorkloadRows.Children.Clear();
        DiskRows.Children.Clear();
        _rows.Clear();
        _io.Clear();
        _cpu.Clear();
        IoSpark.Reset();
        CpuSpark.Reset();
        _headlineShown = 0;

        RunInfo? info = ViewModel.State.Info;
        if (info is not null)
        {
            foreach (TestDescriptor test in info.Tests)
            {
                var row = new ComparisonRow(test, compact: !test.IsWorkload);
                _rows[test.Id] = row;
                (test.IsWorkload ? WorkloadRows : DiskRows).Children.Add(row);
            }

            SystemLetterText.Text = info.SystemDrive.Letter;
            SystemCaptionText.Text = DriveCaption(info.SystemDrive);
            DevLetterText.Text = info.DevDrive.Letter;
            DevCaptionText.Text = DriveCaption(info.DevDrive);
            MachineText.Text = MachineCaption(info.Machine);
        }

        WorkloadHeader.Visibility = WorkloadRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiskHeader.Visibility = DiskRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _dirtyFrames = 60;
    }

    private void OnFrame(object? sender, object e)
    {
        ViewModel.Pump();
        _frame++;

        // ~30 fps while running (keeps our own CPU out of the measurement); idle frames only while settling.
        bool running = ViewModel.IsRunning;
        if (running ? (_frame & 1) == 1 : _dirtyFrames-- <= 0)
        {
            return;
        }

        RunState state = ViewModel.State;
        RunInfo? info = state.Info;
        double now = ViewModel.Now;

        if (info is not null)
        {
            foreach (TestState t in state.Tests)
            {
                if (_rows.TryGetValue(t.Id, out ComparisonRow? row))
                {
                    row.Render(t, info, now, _comparison.GetValueOrDefault(t.Id));
                }
            }
        }

        RenderHeadline(state, info);
        RenderNow(state, info, now, running);
        Ribbon.Render(state.Timeline, running ? state.OpenPhase : null, now);

        double sparkNow = _io.Count > 0 ? Math.Max(now, _io[^1].T) : now;
        IoSpark.Render(_io, sparkNow, ActivityWindowSeconds);
        CpuSpark.Render(_cpu, sparkNow, ActivityWindowSeconds);
        Set(IoValueText, _io.Count > 0 ? Fmt.Throughput(_io[^1].Value) : string.Empty);
        IoValueText.Foreground = _io.Count > 0 ? (_io[^1].Series switch { 2 => Theme.DevDrive, 1 => Theme.SystemDrive, _ => Theme.Secondary }) : Theme.Secondary;
        Set(CpuValueText, _cpu.Count > 0 ? $"{_cpu[^1].Value:0}%" : string.Empty);
    }

    private void RenderHeadline(RunState state, RunInfo? info)
    {
        if (state.Headline is double target)
        {
            // Count up toward the value as workloads land.
            _headlineShown = _headlineShown <= 0 ? 1 : _headlineShown + ((target - _headlineShown) * 0.12);
            if (Math.Abs(target - _headlineShown) < 0.005)
            {
                _headlineShown = target;
            }
            else
            {
                _dirtyFrames = Math.Max(_dirtyFrames, 2);
            }

            Set(HeadlineText, Fmt.Headline(_headlineShown));
            HeadlineText.Foreground = target >= 1 ? Theme.DevDrive : Theme.SystemDrive;
            Set(HeadlineCaption, target >= 1 ? "faster dev workloads on the Dev Drive" : "Dev Drive relative speed on dev workloads");
            int n = state.HeadlineCount;
            int runs = info?.Iterations ?? 1;
            string detail = $"median of {n} workload{(n == 1 ? string.Empty : "s")} · {runs} run{(runs == 1 ? string.Empty : "s")} per drive{(runs >= 2 ? " · warm-up dropped" : string.Empty)}";
            if (info is not null && info.Tests.Any(x => x.IsWorkload && x.Group != TestCatalog.GitClone))
            {
                detail += $" · packages: {TestCatalog.PackagesMode(info.Packages, info)}";
                if (ViewModel.Comparison is { Headline: double other } c)
                {
                    detail += $"\n{Fmt.Ratio(other)} with packages: {TestCatalog.PackagesMode(c.Packages, info)}";
                }
            }

            Set(HeadlineDetail, detail);
        }
        else
        {
            Set(HeadlineText, string.Empty);
            HeadlineText.Foreground = Theme.Tertiary;
            Set(HeadlineCaption, "Dev Drive advantage on dev workloads");
            string pendingDetail = info is null ? string.Empty : $"{info.DevDrive.Letter} Dev Drive vs {info.SystemDrive.Letter} system drive · same machine, same tools";
            if (info is not null && info.Tests.Any(x => x.IsWorkload && x.Group != TestCatalog.GitClone))
            {
                pendingDetail = $"{info.DevDrive.Letter} Dev Drive vs {info.SystemDrive.Letter} system drive · packages: {TestCatalog.PackagesMode(info.Packages, info)}";
                if (ViewModel.Comparison is { Headline: double earlier } c)
                {
                    pendingDetail += $"\n{Fmt.Ratio(earlier)} with packages: {TestCatalog.PackagesMode(c.Packages, info)}";
                }
            }

            Set(HeadlineDetail, pendingDetail);
        }

        AutomationSet(HeadlineText, state.Headline is double h ? $"Dev Drive advantage {Fmt.Ratio(h)}" : "No result yet");
    }

    private void RenderNow(RunState state, RunInfo? info, double now, bool running)
    {
        string text;
        Brush dot = Theme.Tertiary;
        double dotOpacity = 1;

        if (state.Outcome is RunOutcome outcome && info is not null)
        {
            // The result shows the moment measuring ends; deleting the run's files continues quietly.
            if (info.Session == SessionKind.Prepare)
            {
                int ready = state.Tests.Count(t => t.Descriptor.IsWorkload && t.Status == TestStatus.Ready);
                int workloads = state.Tests.Count(t => t.Descriptor.IsWorkload);
                text = outcome switch
                {
                    RunOutcome.Completed => $"Ready  ·  {(ready == workloads ? $"{ready}" : $"{ready} of {workloads}")} workload{(workloads == 1 ? string.Empty : "s")} prepared in {Fmt.Clock(state.LastEventTime)}  ·  F5 to run",
                    RunOutcome.Failed => "Preparation failed  ·  " + state.Error,
                    _ => $"Preparation stopped  ·  {Fmt.Clock(state.LastEventTime)}",
                };
            }
            else
            {
                text = outcome switch
                {
                    RunOutcome.Completed => $"Done  ·  {Fmt.Clock(state.LastEventTime)}",
                    RunOutcome.Cancelled => $"Stopped  ·  {Fmt.Clock(state.LastEventTime)}",
                    _ => "Failed  ·  " + state.Error,
                };
            }

            dot = outcome == RunOutcome.Completed ? Theme.DevDrive : Theme.SystemDrive;
            Set(NowText, text);
            NowDot.Fill = dot;
            NowDot.Opacity = 1;
            Set(ElapsedText, Fmt.Clock(state.LastEventTime));
            return;
        }

        if (running && state.OpenPhase is PhaseStartedEvent p && info is not null && state.Find(p.TestId) is TestState t)
        {
            string letter = p.Drive == DriveRole.Dev ? info.DevDrive.Letter : info.SystemDrive.Letter;
            string run = info.Iterations >= 2 && p.Iteration > 0 ? $"  ·  run {p.Iteration} of {info.Iterations}" : string.Empty;

            // Name the variable that placed this run's packages (benchmark processes only), e.g. "NUGET_PACKAGES on C:".
            string packages = (t.Descriptor.PackageVariable ?? TestCatalog.PackageVariable(t.Descriptor.Group)) is string variable && t.Descriptor.IsWorkload && p.Drive != DriveRole.None
                ? $"  ·  {variable} on {(p.Drive == DriveRole.Dev ? TestCatalog.PackagesLetter(info.Packages, info) : info.SystemDrive.Letter)}"
                : string.Empty;
            text = p.Phase switch
            {
                RunPhase.Prepare => $"{t.Descriptor.Name}  ·  preparing fixture, staging packages  ·  {Fmt.Clock(now - p.T)}",
                RunPhase.Setup => $"{t.Descriptor.Name}  ·  {letter}  ·  setup (untimed){run}",
                RunPhase.Measure => $"{t.Descriptor.Name}  ·  {letter}{packages}{run}",
                _ => $"{t.Descriptor.Name}  ·  cleanup",
            };
            dot = p.Drive switch { DriveRole.Dev => Theme.DevDrive, DriveRole.System => Theme.SystemDrive, _ => Theme.Secondary };
            dotOpacity = 0.35 + (0.65 * Math.Abs(Math.Sin(now * Math.PI)));
        }
        else if (running)
        {
            text = info?.Session == SessionKind.Prepare ? "Preparing" : ViewModel.IsShowingReplay ? "Replaying" : "Starting";
            dotOpacity = 0.35 + (0.65 * Math.Abs(Math.Sin(now * Math.PI)));
        }
        else if (!ViewModel.IsReady)
        {
            text = "Probing drives and tools";
        }
        else if (info is null)
        {
            text = ViewModel.IsReplayMode ? "No recording selected" : "No Dev Drive found  ·  use Replay";
        }
        else
        {
            int tests = info.Tests.Select(x => x.Group).Distinct().Count();
            string testsText = $"{tests} test{(tests == 1 ? string.Empty : "s")}";
            string runsText = $"{info.Iterations} run{(info.Iterations == 1 ? string.Empty : "s")} per drive";
            text = (ViewModel.IsShowingReplay, ViewModel.IsPrepared) switch
            {
                (true, true) => $"Ready to replay  ·  {testsText}  ·  F5",
                (true, false) => $"Replay  ·  {testsText}{(ViewModel.CanPrepare ? "  ·  Prepare, then F5" : "  ·  F5")}",
                (false, true) => $"Ready  ·  {testsText} prepared  ·  {runsText}  ·  F5",
                _ => $"{testsText}  ·  {runsText}  ·  Prepare, then F5",
            };
            if (ViewModel.IsPrepared)
            {
                dot = Theme.DevDrive;
            }
        }

        Set(NowText, text);
        NowDot.Fill = dot;
        NowDot.Opacity = dotOpacity;
        Set(ElapsedText, running || state.Outcome is not null ? Fmt.Clock(now) : string.Empty);
    }

    private static string DriveCaption(DriveDescriptor d)
    {
        var parts = new List<string> { d.FileSystem };
        if (d.IsSystem)
        {
            parts.Add("system");
        }

        if (d.IsDevDrive)
        {
            parts.Add(d.IsTrusted ? "Dev Drive · trusted" : "Dev Drive");
        }

        if (d.IsVirtual)
        {
            string host = d.BackingFile is { Length: >= 2 } b ? $" on {b[..2].ToUpperInvariant()}" : string.Empty;
            parts.Add("VHDX" + host);
        }
        else if (!string.IsNullOrEmpty(d.BusType) && d.BusType != "Unknown")
        {
            parts.Add(d.BusType);
        }

        return string.Join("  ·  ", parts.Where(x => !string.IsNullOrEmpty(x)));
    }

    private static string MachineCaption(MachineInfo m)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(m.Name))
        {
            parts.Add(m.Name);
        }

        if (!string.IsNullOrEmpty(m.Cpu))
        {
            parts.Add(m.Cpu);
        }

        if (m.MemoryBytes > 0)
        {
            parts.Add($"{Math.Round(m.MemoryBytes / 1073741824d):0} GB");
        }

        return string.Join("  ·  ", parts);
    }

    private static void Set(TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal))
        {
            block.Text = text;
        }
    }

    private static void AutomationSet(UIElement element, string name)
    {
        if (!string.Equals(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element), name, StringComparison.Ordinal))
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
        }
    }

    private void SyncSelectors(string? property)
    {
        _syncing = true;
        try
        {
            if (property is null or nameof(MainViewModel.IsReplayMode))
            {
                ModeBar.SelectedItem = ViewModel.IsReplayMode ? ReplayItem : LiveItem;
            }

            if (property is null or nameof(MainViewModel.SelectedRecording))
            {
                RecordingBox.SelectedItem = ViewModel.SelectedRecording;
            }

            if (property is null or nameof(MainViewModel.DevDrive))
            {
                DevDriveBox.SelectedItem = ViewModel.DevDrive;
            }

            if (property is null or nameof(MainViewModel.IsThorough))
            {
                ProfileButtons.SelectedIndex = ViewModel.IsThorough ? 0 : 1;
            }

            if (property is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.IsPrepared) or nameof(MainViewModel.CanPrepare))
            {
                _dirtyFrames = 60;
            }

            if (property is null or nameof(MainViewModel.Comparison))
            {
                _comparison = ViewModel.Comparison?.Results.ToDictionary(r => r.TestId, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, TestResult>(StringComparer.OrdinalIgnoreCase);
                _dirtyFrames = 60;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        if (ViewModel.IsRunning)
        {
            // Mode is fixed for the duration of a run (UIA can select even a disabled SelectorBar).
            SyncSelectors(nameof(MainViewModel.IsReplayMode));
            return;
        }

        ViewModel.IsReplayMode = sender.SelectedItem == ReplayItem;
    }

    private void OnRecordingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && RecordingBox.SelectedItem is RecordingItem item)
        {
            ViewModel.SelectedRecording = item;
        }
    }

    private void OnDevDriveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && DevDriveBox.SelectedItem is DriveDescriptor drive)
        {
            ViewModel.DevDrive = drive;
        }
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && ProfileButtons.SelectedIndex >= 0)
        {
            ViewModel.IsThorough = ProfileButtons.SelectedIndex == 0;
        }
    }

    private void OnRunAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.IsRunning && ViewModel.CanRun)
        {
            ViewModel.RunOrStopCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void OnPrepareAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CanPrepare)
        {
            ViewModel.PrepareCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void OnStopAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Esc closes an open menu, flyout or dropdown first; it only stops a run when nothing is open.
        if (ViewModel.IsRunning && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count == 0)
        {
            ViewModel.RunOrStopCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void OnFullScreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        (App.Window as MainWindow)?.ToggleFullScreen();
        args.Handled = true;
    }
}
