using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Platform;
using DevDriveBench.Engine.Run;
using Microsoft.UI.Dispatching;

namespace DevDriveBench.ViewModels;

/// <summary>A selectable test group in the plan flyout, with a winget install for its tools.</summary>
public sealed partial class TestToggle : ObservableObject
{
    public TestToggle(string group, string name, bool available, string? note, IReadOnlyList<WingetPackage> packages, Func<TestToggle, Task> install)
    {
        Group = group;
        Name = name;
        Packages = packages;
        InstallCommand = new AsyncRelayCommand(() => install(this), () => CanInstall);
        IsAvailable = available;
        Note = note ?? string.Empty;
        IsSelected = available;
    }

    public string Group { get; }

    public string Name { get; }

    public IReadOnlyList<WingetPackage> Packages { get; }

    public IAsyncRelayCommand InstallCommand { get; }

    /// <summary>What the Install button will run, for its tooltip.</summary>
    public string InstallTooltip => Packages.Count == 0
        ? string.Empty
        : "winget install " + string.Join(" + ", Packages.Select(p => p.Id));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsInstalling { get; set; }

    /// <summary>Latest installer status line (empty when idle).</summary>
    [ObservableProperty]
    public partial string InstallStatus { get; set; } = string.Empty;

    /// <summary>True when the tool is missing, winget can fetch it, and no install is running.</summary>
    public bool CanInstall => !IsAvailable && !IsInstalling && Packages.Count > 0;

    public bool HasInstallStatus => InstallStatus.Length > 0;

    public string InstallButtonName => $"Install {Name} tools";

    partial void OnInstallStatusChanged(string value) => OnPropertyChanged(nameof(HasInstallStatus));

    partial void OnIsAvailableChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();

    partial void OnIsInstallingChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();
}

/// <summary>One recording in the Replay picker.</summary>
public sealed class RecordingItem
{
    public RecordingItem(RecordingSummary summary) => Summary = summary;

    public RecordingSummary Summary { get; }

    public string Display
    {
        get
        {
            RecordingSummary s = Summary;
            string when = s.StartedAt.LocalDateTime.ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
            string headline = s.Headline is double h ? $" · {h.ToString("0.0", CultureInfo.CurrentCulture)}×" : string.Empty;
            string tag = s.IsBundled ? "sample · " : string.Empty;
            string outcome = s.Outcome == RunOutcome.Completed ? string.Empty : $" · {s.Outcome.ToString().ToLowerInvariant()}";
            return $"{tag}{when} · {s.Machine} · {s.SystemDrive} vs {s.DevDrive} · {s.Iterations}×run{headline}{outcome}";
        }
    }

    public override string ToString() => Display;
}

/// <summary>
/// Owns the plan (drives, runs, tests), the Live and Replay sessions, and the single
/// <see cref="RunState"/> both modes fold events into. The page renders from <see cref="State"/>
/// every frame; this class never touches visuals.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly BenchRunner _runner = BenchRunner.CreateDefault();
    private readonly Stopwatch _replayClock = new();
    private IReadOnlyList<TestDescriptor> _described = Array.Empty<TestDescriptor>();
    private MachineInfo _machine = new() { Name = Environment.MachineName };
    private ReplayScheduler? _replay;
    private double _replayOffset;
    private CancellationTokenSource? _cts;
    private bool _isLiveSession;
    private bool _revertingMode;
    private bool _wingetAvailable;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>The run being shown (live, replayed, or the idle plan preview).</summary>
    public RunState State { get; private set; } = new();

    /// <summary>Raised on the UI thread whenever <see cref="State"/> is replaced (new run or preview).</summary>
    public event EventHandler? StateReset;

    /// <summary>Raised on the UI thread for every applied event (after it is folded into <see cref="State"/>).</summary>
    public event EventHandler<BenchEvent>? EventApplied;

    public ObservableCollection<TestToggle> Tests { get; } = new();

    public ObservableCollection<RecordingItem> Recordings { get; } = new();

    public ObservableCollection<DriveDescriptor> DevDriveChoices { get; } = new();

    public IReadOnlyList<string> ReplaySpeeds { get; } = new[] { "1×", "2×", "4×", "8×" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveMode), nameof(CanRun))]
    public partial bool IsReplayMode { get; set; }

    public bool IsLiveMode => !IsReplayMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(RunLabel), nameof(RunGlyph), nameof(IsIdle))]
    public partial bool IsRunning { get; set; }

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial bool IsReady { get; set; }

    /// <summary>True while any winget install runs (Run waits, so installers never overlap a measurement).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial bool IsInstalling { get; set; }

    /// <summary>Iterations per drive per test; default 1 (fast). With ≥ 2 the first is discarded.</summary>
    [ObservableProperty]
    public partial double Iterations { get; set; } = 1;

    /// <summary>True = Thorough fixtures (15,000-file git repo); false = Quick (1,200 files).</summary>
    [ObservableProperty]
    public partial bool IsThorough { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial RecordingItem? SelectedRecording { get; set; }

    [ObservableProperty]
    public partial int ReplaySpeedIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(HasDevDrive))]
    public partial DriveDescriptor? DevDrive { get; set; }

    [ObservableProperty]
    public partial DriveDescriptor? SystemDrive { get; set; }

    /// <summary>One short line under the rows: where the data went, or what is being replayed.</summary>
    [ObservableProperty]
    public partial string FooterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? LastSavedPath { get; set; }

    public bool HasDevDrive => DevDrive is not null;

    public bool CanRun => IsRunning || (IsReady && !IsInstalling && (IsReplayMode ? SelectedRecording is not null : DevDrive is not null && SystemDrive is not null));

    public string RunLabel => IsRunning ? "Stop" : "Run";

    public string RunGlyph => IsRunning ? "\uE71A" : "\uE768";

    public double ReplaySpeed => ReplaySpeedIndex switch { 1 => 2, 2 => 4, 3 => 8, _ => 1 };

    /// <summary>True while showing a replay (live or finished), so the page can label it honestly.</summary>
    [ObservableProperty]
    public partial bool IsShowingReplay { get; set; }

    /// <summary>
    /// Current run time in seconds: the engine's clock while live, the scaled playback clock while
    /// replaying, otherwise the last event time.
    /// </summary>
    public double Now
    {
        get
        {
            if (IsRunning && _isLiveSession)
            {
                return _runner.Elapsed.TotalSeconds;
            }

            if (IsRunning && _replay is not null)
            {
                return Math.Min(_replayOffset + (_replayClock.Elapsed.TotalSeconds * ReplaySpeed), _replay.Duration);
            }

            return State.LastEventTime;
        }
    }

    /// <summary>Probes drives, tools, and recordings off the UI thread, then shows the plan.</summary>
    public async Task InitializeAsync()
    {
        IReadOnlyList<DriveDescriptor> drives = Array.Empty<DriveDescriptor>();
        await Task.Run(() =>
        {
            EnvironmentRefresher.RefreshPath();
            drives = DriveProbe.GetFixedDrives();
            _described = _runner.DescribeTests(WorkloadProfile.Thorough);
            _machine = MachineProbe.Capture();
            _wingetAvailable = WingetInstaller.IsAvailable;
        });

        // Counter initialization takes seconds once per process; pay it now, not at the first Run.
        _ = Task.Run(PerfCounterActivitySource.Warm);

        SystemDrive = drives.FirstOrDefault(d => d.IsSystem);
        foreach (DriveDescriptor d in drives.Where(d => d.IsDevDrive))
        {
            DevDriveChoices.Add(d);
        }

        DevDrive = DevDriveChoices.FirstOrDefault(d => d.IsTrusted) ?? DevDriveChoices.FirstOrDefault();

        foreach (string group in TestCatalog.GroupOrder)
        {
            TestDescriptor[] rows = _described.Where(t => t.Group == group).ToArray();
            if (rows.Length == 0)
            {
                continue;
            }

            bool available = rows.All(r => r.Available);
            IReadOnlyList<WingetPackage> packages = _wingetAvailable ? InstallCatalog.For(group) : Array.Empty<WingetPackage>();
            var toggle = new TestToggle(group, TestCatalog.GroupName(group), available, GroupNote(group, rows[0]), packages, InstallAsync);
            toggle.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TestToggle.IsSelected) && !IsRunning && !IsShowingReplay)
                {
                    ShowPreview();
                }
            };
            Tests.Add(toggle);
        }

        RefreshRecordings();
        IsReplayMode = DevDrive is null && Recordings.Count > 0;
        IsReady = true;
        ShowPreview();
    }

    partial void OnIsReplayModeChanged(bool oldValue, bool newValue)
    {
        if (_revertingMode)
        {
            return;
        }

        if (IsRunning)
        {
            _revertingMode = true;
            IsReplayMode = oldValue;
            _revertingMode = false;
            return;
        }

        ShowPreview();
    }

    partial void OnSelectedRecordingChanged(RecordingItem? value)
    {
        if (!IsRunning && IsReplayMode)
        {
            ShowPreview();
        }
    }

    partial void OnIsThoroughChanged(bool value)
    {
        if (!IsRunning && !IsShowingReplay)
        {
            ShowPreview();
        }
    }

    partial void OnIterationsChanged(double value)
    {
        if (double.IsNaN(value) || value < 1)
        {
            Iterations = 1;
        }
        else if (value > 25)
        {
            Iterations = 25;
        }
        else if (value != Math.Round(value))
        {
            Iterations = Math.Round(value);
        }
        else if (!IsRunning && !IsShowingReplay)
        {
            ShowPreview();
        }
    }

    partial void OnDevDriveChanged(DriveDescriptor? value)
    {
        if (IsReady && !IsRunning)
        {
            ShowPreview();
        }
    }

    partial void OnReplaySpeedIndexChanged(int value)
    {
        // Re-anchor so changing speed mid-replay continues from the current position.
        if (IsRunning && _replay is not null)
        {
            _replayOffset = Now;
            _replayClock.Restart();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunOrStopAsync()
    {
        if (IsRunning)
        {
            Stop();
            return;
        }

        if (IsReplayMode)
        {
            StartReplay();
        }
        else
        {
            await RunLiveAsync();
        }
    }

    [RelayCommand]
    private void OpenRunsFolder()
    {
        string folder = RecordingStore.DefaultDirectory;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    /// <summary>Called by the page every frame: pumps due replay events into the state.</summary>
    public void Pump()
    {
        if (!IsRunning || _replay is null || _isLiveSession)
        {
            return;
        }

        foreach (BenchEvent e in _replay.Advance(Now))
        {
            Apply(e);
        }

        if (_replay.IsFinished)
        {
            _replayClock.Stop();
            IsRunning = false;
        }
    }

    private async Task RunLiveAsync()
    {
        if (SystemDrive is null || DevDrive is null)
        {
            return;
        }

        var plan = new BenchPlan
        {
            SystemDrive = SystemDrive,
            DevDrive = DevDrive,
            Iterations = (int)Iterations,
            Profile = IsThorough ? WorkloadProfile.Thorough : WorkloadProfile.Quick,
            Groups = Tests.Where(t => t.IsSelected && t.IsAvailable).Select(t => t.Group).ToArray(),
        };

        IReadOnlyList<TestDescriptor> tests = DescribeFor(plan.Profile);
        _cts = new CancellationTokenSource();
        _isLiveSession = true;
        _replay = null;
        IsShowingReplay = false;
        IsRunning = true;
        LastSavedPath = null;
        FooterText = "Recording";
        ResetState();

        RunRecording recording;
        try
        {
            recording = await _runner.RunAsync(plan, e => _dispatcher.TryEnqueue(() => Apply(e)), tests, _cts.Token);
        }
        finally
        {
            _isLiveSession = false;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }

        try
        {
            string path = await Task.Run(() => RecordingStore.Save(recording));
            LastSavedPath = path;
            FooterText = $"Saved  {Path.GetFileName(path)}  +  .csv";
            RefreshRecordings();
        }
        catch (Exception ex)
        {
            FooterText = "Could not save the recording: " + ex.Message;
        }
    }

    private void StartReplay()
    {
        if (SelectedRecording is null)
        {
            return;
        }

        RunRecording recording;
        try
        {
            recording = RecordingStore.Load(SelectedRecording.Summary.Path);
        }
        catch (Exception ex)
        {
            FooterText = "Could not load the recording: " + ex.Message;
            return;
        }

        _replay = new ReplayScheduler(recording.Events);
        _replayOffset = 0;
        _isLiveSession = false;
        IsShowingReplay = true;
        FooterText = ReplayFooter(recording.Info);
        ResetState();
        IsRunning = true;
        _replayClock.Restart();
        Pump();
    }

    private void Stop()
    {
        if (_isLiveSession)
        {
            _cts?.Cancel();
            FooterText = "Stopping · cleaning up";
            return;
        }

        _replayClock.Stop();
        IsRunning = false;
    }

    private void Apply(BenchEvent e)
    {
        State.Apply(e);
        EventApplied?.Invoke(this, e);
    }

    private void ResetState()
    {
        State = new RunState();
        StateReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the rows a Run would produce (pending), so the stage is never empty.</summary>
    private void ShowPreview()
    {
        if (!IsReady)
        {
            return;
        }

        IsShowingReplay = false;
        RunInfo? info = null;
        if (IsReplayMode)
        {
            if (SelectedRecording is not null)
            {
                try
                {
                    RunRecording r = RecordingStore.Load(SelectedRecording.Summary.Path);
                    info = r.Info;
                    IsShowingReplay = true;
                    FooterText = ReplayFooter(r.Info);
                }
                catch (Exception ex)
                {
                    FooterText = "Could not load the recording: " + ex.Message;
                }
            }
        }
        else if (SystemDrive is not null && DevDrive is not null)
        {
            WorkloadProfile profile = IsThorough ? WorkloadProfile.Thorough : WorkloadProfile.Quick;
            var groups = new HashSet<string>(Tests.Where(t => t.IsSelected && t.IsAvailable).Select(t => t.Group));
            info = new RunInfo
            {
                SystemDrive = SystemDrive,
                DevDrive = DevDrive,
                Iterations = (int)Iterations,
                Profile = profile,
                Tests = DescribeFor(profile).Where(t => groups.Contains(t.Group)).ToArray(),
                Machine = _machine,
            };
            FooterText = LastSavedPath is null ? $"Runs are saved to {RecordingStore.DefaultDirectory}" : $"Saved  {Path.GetFileName(LastSavedPath)}  +  .csv";
        }
        else
        {
            FooterText = "No Dev Drive found · use Replay";
        }

        ResetState();
        if (info is not null)
        {
            Apply(new RunStartedEvent(info));
        }
    }

    private IReadOnlyList<TestDescriptor> DescribeFor(WorkloadProfile profile) =>
        _described
            .Select(t => t.Group == TestCatalog.GitClone
                ? t with { Caption = TestCatalog.WorkloadCaption(t.Group, profile) }
                : t)
            .ToArray();

    private void RefreshRecordings()
    {
        string? previous = SelectedRecording?.Summary.Path;
        string bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Replays");
        IReadOnlyList<RecordingSummary> list = RecordingStore.List(RecordingStore.DefaultDirectory, bundled);

        Recordings.Clear();
        foreach (RecordingSummary s in list)
        {
            Recordings.Add(new RecordingItem(s));
        }

        // Prefer the newest completed real run; fall back to the bundled sample.
        SelectedRecording = Recordings.FirstOrDefault(r => r.Summary.Path == previous)
            ?? Recordings.FirstOrDefault(r => r.Summary.Outcome == RunOutcome.Completed)
            ?? Recordings.FirstOrDefault();
    }

    private static string ReplayFooter(RunInfo info) =>
        $"Replay of a recorded run · {info.StartedAt.LocalDateTime.ToString("MMM d yyyy HH:mm", CultureInfo.CurrentCulture)} · {info.Machine.Name}";

    private string? GroupNote(string group, TestDescriptor first)
    {
        if (!first.Available)
        {
            return first.UnavailableReason;
        }

        string emulated = TestCatalog.EmulationNote(first, _machine) is string note ? note + " · " : string.Empty;
        string tool = first.RequiredTool is string t ? $"{t} {first.ToolVersion}".Trim() + " · " + emulated : string.Empty;
        return group switch
        {
            TestCatalog.DiskGroup => "seconds",
            TestCatalog.GitClone => tool + "offline · seconds",
            _ => tool + "network for first prep · minutes",
        };
    }

    /// <summary>
    /// Installs a workload's tools with winget, then re-probes every tool (PATH is refreshed by the
    /// installer) so the workload becomes runnable without restarting the app.
    /// </summary>
    private async Task InstallAsync(TestToggle toggle)
    {
        toggle.IsInstalling = true;
        IsInstalling = true;
        toggle.InstallStatus = "starting winget";
        try
        {
            var progress = new Progress<string>(line => toggle.InstallStatus = line);
            InstallResult result = await new WingetInstaller().InstallAsync(toggle.Packages, progress);

            toggle.InstallStatus = "checking";
            _described = await Task.Run(() =>
            {
                EnvironmentRefresher.RefreshPath();
                return _runner.DescribeTests(WorkloadProfile.Thorough);
            });

            foreach (TestToggle t in Tests)
            {
                TestDescriptor[] rows = _described.Where(d => d.Group == t.Group).ToArray();
                bool available = rows.Length > 0 && rows.All(r => r.Available);
                bool becameAvailable = available && !t.IsAvailable;
                t.IsAvailable = available;
                t.Note = rows.Length > 0 ? GroupNote(t.Group, rows[0]) ?? string.Empty : t.Note;
                if (becameAvailable)
                {
                    t.IsSelected = true;
                }
            }

            toggle.InstallStatus = result.Succeeded
                ? (toggle.IsAvailable ? "installed · ready" : "installed · not on PATH yet, restart the app")
                : "failed · " + result.Message;
        }
        catch (Exception ex)
        {
            toggle.InstallStatus = "failed · " + ex.Message;
        }
        finally
        {
            toggle.IsInstalling = false;
            IsInstalling = Tests.Any(t => t.IsInstalling);
            if (!IsRunning && !IsShowingReplay)
            {
                ShowPreview();
            }
        }
    }
}
