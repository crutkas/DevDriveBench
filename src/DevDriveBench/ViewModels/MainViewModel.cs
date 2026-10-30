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
            string packages = s.Packages == PackageLocation.SystemDrive ? $" · pkgs {s.SystemDrive}" : " · pkgs optimal";
            return $"{tag}{when} · {s.Machine} · {s.SystemDrive} vs {s.DevDrive}{packages} · {s.Iterations}×run{headline}{outcome}";
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
    private Task? _sessionTask;
    private RunRecording? _lastPreparation;
    private RunRecording? _selectedPreparation;
    private string? _replayPreparedPath;
    private bool _replayingPreparation;

    // This session's latest completed live run for each package location, paired for comparison.
    private readonly Dictionary<PackageLocation, RunRecording> _lastLive = new();
    private DateTime _sessionStartedAt;

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
    [NotifyPropertyChangedFor(nameof(IsLiveMode), nameof(CanRun), nameof(CanPrepare))]
    public partial bool IsReplayMode { get; set; }

    public bool IsLiveMode => !IsReplayMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(RunLabel), nameof(RunGlyph), nameof(IsIdle), nameof(CanPrepare))]
    public partial bool IsRunning { get; set; }

    /// <summary>
    /// Live run measured and on screen, its files still being deleted: Run shows (disabled) instead of
    /// Stop, since there is nothing left to stop.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(RunLabel), nameof(RunGlyph))]
    public partial bool IsFinishing { get; set; }

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanPrepare))]
    public partial bool IsReady { get; set; }

    /// <summary>True while any winget install runs (Run waits, so installers never overlap a measurement).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanPrepare))]
    public partial bool IsInstalling { get; set; }

    /// <summary>
    /// Every selected workload's fixture is prepared for the current tools and profile (Live), or the
    /// recording's preparation has been played (Replay): Run goes straight to measuring.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrepare), nameof(ShowPrepareButton))]
    public partial bool IsPrepared { get; set; }

    public bool ShowPrepareButton => !IsPrepared && !(IsReplayMode && _selectedPreparation is null);

    /// <summary>True when Prepare has something to do (Live) or something to play (Replay).</summary>
    public bool CanPrepare => IsReady && !IsRunning && !IsInstalling && !IsPrepared &&
        (IsReplayMode ? _selectedPreparation is not null : DevDrive is not null && SystemDrive is not null);

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
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(HasDevDrive), nameof(CanPrepare), nameof(DevLetter))]
    public partial DriveDescriptor? DevDrive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemLetter))]
    public partial DriveDescriptor? SystemDrive { get; set; }

    /// <summary>
    /// Optimal drive (default): every run's package caches are on its own drive. C drive: the Dev Drive
    /// run reads them from the system drive ("moved the code, not the packages"). Applied through
    /// <c>npm_config_cache</c>, <c>NUGET_PACKAGES</c> and <c>CARGO_HOME</c> on the benchmark's own child
    /// processes only; the user's and machine's environment variables are never changed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Packages))]
    public partial bool PackagesOnOptimalDrive { get; set; } = true;

    public PackageLocation Packages => PackagesOnOptimalDrive ? PackageLocation.DevDrive : PackageLocation.SystemDrive;

    public string SystemLetter => SystemDrive?.Letter ?? "C:";

    public string DevLetter => DevDrive?.Letter ?? "Dev Drive";

    /// <summary>
    /// The latest run with the packages on the other drive (same drives), shown next to the current one:
    /// from this session's live runs, or embedded in the replayed recording.
    /// </summary>
    [ObservableProperty]
    public partial ComparisonRun? Comparison { get; set; }

    /// <summary>One short line under the rows: where the data went, or what is being replayed.</summary>
    [ObservableProperty]
    public partial string FooterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? LastSavedPath { get; set; }

    public bool HasDevDrive => DevDrive is not null;

    public bool CanRun => IsFinishing
        ? false
        : IsRunning || (IsReady && !IsInstalling && (IsReplayMode ? SelectedRecording is not null : DevDrive is not null && SystemDrive is not null));

    public string RunLabel => IsRunning && !IsFinishing ? "Stop" : "Run";

    public string RunGlyph => IsRunning && !IsFinishing ? "\uE71A" : "\uE768";

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

    partial void OnPackagesOnOptimalDriveChanged(bool value)
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
            // A double-click on Run must not stop the run it just started.
            if (!IsFinishing && DateTime.UtcNow - _sessionStartedAt >= TimeSpan.FromSeconds(1))
            {
                Stop();
            }

            return;
        }

        if (IsReplayMode)
        {
            StartReplay(preparation: false);
        }
        else
        {
            await LiveSessionAsync(SessionKind.Run);
        }
    }

    /// <summary>
    /// Live: prepares every selected workload's fixture now and keeps it, so Run measures straight
    /// away. Replay: plays the recording's preparation, so the stage flow is the same.
    /// </summary>
    [RelayCommand]
    private async Task PrepareAsync()
    {
        if (!CanPrepare)
        {
            return;
        }

        if (IsReplayMode)
        {
            StartReplay(preparation: true);
        }
        else
        {
            await LiveSessionAsync(SessionKind.Prepare);
        }
    }

    /// <summary>Deletes every kept fixture (they are also deleted when the app closes).</summary>
    [RelayCommand]
    private async Task ClearPreparedAsync()
    {
        if (IsRunning)
        {
            return;
        }

        FooterText = "Deleting prepared fixtures";
        await Task.Run(_runner.ReleasePrepared);
        _lastPreparation = null;
        UpdatePreparedState();
        FooterText = "Prepared fixtures deleted";
        if (!IsShowingReplay)
        {
            ShowPreview();
        }
    }

    /// <summary>True when closing the app must first delete kept fixtures or stop a session.</summary>
    public bool NeedsShutdown => IsRunning || _runner.HasPreparedSeeds;

    /// <summary>Stops any session and deletes every kept fixture; called when the window closes.</summary>
    public async Task ShutdownAsync()
    {
        _cts?.Cancel();
        if (_sessionTask is Task running)
        {
            try
            {
                await running;
            }
            catch
            {
                // Cancellation/failure: cleanup below still runs.
            }
        }

        await Task.Run(_runner.ReleasePrepared);
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
            if (_replayingPreparation && State.Outcome == RunOutcome.Completed)
            {
                _replayPreparedPath = SelectedRecording?.Summary.Path;
            }

            _replayingPreparation = false;
            UpdatePreparedState();
        }
    }

    private async Task LiveSessionAsync(SessionKind kind)
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
            Packages = Packages,
        };

        IReadOnlyList<TestDescriptor> tests = DescribeFor(plan.Profile);
        _cts = new CancellationTokenSource();
        _isLiveSession = true;
        _replay = null;
        IsShowingReplay = false;
        _sessionStartedAt = DateTime.UtcNow;
        IsRunning = true;
        LastSavedPath = null;
        FooterText = kind == SessionKind.Prepare ? "Preparing fixtures" : "Recording";
        ResetState();

        RunRecording recording;
        try
        {
            Action<BenchEvent> sink = e => _dispatcher.TryEnqueue(() => Apply(e));
            Task<RunRecording> session = kind == SessionKind.Prepare
                ? _runner.PrepareAsync(plan, sink, tests, _cts.Token)
                : _runner.RunAsync(plan, sink, tests, _cts.Token);
            _sessionTask = session;
            recording = await session;
        }
        finally
        {
            _sessionTask = null;
            _isLiveSession = false;
            IsFinishing = false;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            UpdatePreparedState();
        }

        if (kind == SessionKind.Prepare)
        {
            if (recording.Events.OfType<TestPreparedEvent>().Any())
            {
                _lastPreparation = recording;
            }

            int prepared = recording.Events.OfType<TestPreparedEvent>().Count(e => !e.TestId.StartsWith(TestCatalog.DiskGroup, StringComparison.Ordinal));
            FooterText = recording.Outcome switch
            {
                RunOutcome.Completed => $"Prepared {prepared} workload{(prepared == 1 ? string.Empty : "s")} in {Fmt(recording.DurationSeconds)} · packages staged on {SystemLetter} and {DevLetter} · Run measures straight away · fixtures are deleted when the app closes",
                RunOutcome.Failed => "Preparation failed · " + recording.Events.OfType<RunCompletedEvent>().LastOrDefault()?.Error,
                _ => "Preparation stopped",
            };
            return;
        }

        if (recording.Info.PreparedAhead && _lastPreparation is not null)
        {
            recording = recording with { Preparation = _lastPreparation };
        }

        if (Comparison is not null)
        {
            recording = recording with { Comparison = Comparison };
        }

        if (recording.Outcome == RunOutcome.Completed)
        {
            _lastLive[recording.Info.Packages] = recording;
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

    private void StartReplay(bool preparation)
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

        RunRecording? played = preparation ? recording.Preparation : recording;
        if (played is null)
        {
            return;
        }

        _replay = new ReplayScheduler(played.Events);
        _replayingPreparation = preparation;
        _replayOffset = 0;
        _isLiveSession = false;
        IsShowingReplay = true;
        Comparison = recording.Comparison;
        FooterText = ReplayFooter(recording.Info);
        ResetState();
        _sessionStartedAt = DateTime.UtcNow;
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
        if (e is RunCompletedEvent && _isLiveSession && State.Info?.Session == SessionKind.Run)
        {
            IsFinishing = true;
            FooterText = "Done · deleting this run's files";
        }

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
        _selectedPreparation = null;
        ComparisonRun? comparison = null;
        if (IsReplayMode)
        {
            if (SelectedRecording is not null)
            {
                try
                {
                    RunRecording r = RecordingStore.Load(SelectedRecording.Summary.Path);
                    info = r.Info;
                    _selectedPreparation = r.Preparation;
                    comparison = r.Comparison;
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
                Packages = Packages,
            };
            PackageLocation other = info.Packages == PackageLocation.DevDrive ? PackageLocation.SystemDrive : PackageLocation.DevDrive;
            if (_lastLive.TryGetValue(other, out RunRecording? earlier) && ComparisonRun.Pairs(info, earlier.Info))
            {
                comparison = ComparisonRun.From(earlier);
            }

            FooterText = LastSavedPath is null ? $"Runs are saved to {RecordingStore.DefaultDirectory}" : $"Saved  {Path.GetFileName(LastSavedPath)}  +  .csv";
        }
        else
        {
            FooterText = "No Dev Drive found · use Replay";
        }

        Comparison = comparison;
        ResetState();
        if (info is not null)
        {
            Apply(new RunStartedEvent(info));
        }

        UpdatePreparedState();
    }

    /// <summary>
    /// Recomputes <see cref="IsPrepared"/>: Live — every selected, available workload has a kept seed
    /// matching its current tool and fixture size; Replay — the selected recording's preparation was played.
    /// </summary>
    private void UpdatePreparedState()
    {
        if (IsReplayMode)
        {
            IsPrepared = _selectedPreparation is not null && _replayPreparedPath is string p && p == SelectedRecording?.Summary.Path;
        }
        else
        {
            WorkloadProfile profile = IsThorough ? WorkloadProfile.Thorough : WorkloadProfile.Quick;
            var groups = new HashSet<string>(Tests.Where(t => t.IsSelected && t.IsAvailable).Select(t => t.Group));
            TestDescriptor[] workloads = _described.Where(d => d.IsWorkload && d.Available && groups.Contains(d.Group)).ToArray();
            // Nothing to prepare (disk passes only) counts as ready: Run measures straight away.
            IsPrepared = workloads.All(d => _runner.IsPrepared(d, profile, SystemDrive?.Root ?? string.Empty, DevDrive?.Root ?? string.Empty));
        }

        OnPropertyChanged(nameof(CanPrepare));
        OnPropertyChanged(nameof(ShowPrepareButton));
    }

    private static string Fmt(double seconds) =>
        seconds < 60 ? $"{seconds:0} s" : $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

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
