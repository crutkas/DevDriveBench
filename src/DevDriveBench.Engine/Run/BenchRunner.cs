using System.Diagnostics;
using System.Reflection;
using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Platform;

namespace DevDriveBench.Engine.Run;

/// <summary>What to run.</summary>
public sealed record BenchPlan
{
    public DriveDescriptor SystemDrive { get; init; } = new();

    public DriveDescriptor DevDrive { get; init; } = new();

    /// <summary>Iterations per drive per test (≥ 1). With ≥ 2 the first is discarded and the median reported.</summary>
    public int Iterations { get; init; } = 1;

    public WorkloadProfile Profile { get; init; } = WorkloadProfile.Thorough;

    /// <summary>Groups to run (see <see cref="TestCatalog.GroupOrder"/>); null runs every available group.</summary>
    public IReadOnlyCollection<string>? Groups { get; init; }

    /// <summary>Where the Dev Drive run reads package caches from (the system-drive run always uses the system drive).</summary>
    public PackageLocation Packages { get; init; } = PackageLocation.DevDrive;

    /// <summary>The drive root holding packages for a run whose code is on <paramref name="role"/>'s drive.</summary>
    public string PackagesRoot(DriveRole role) =>
        role == DriveRole.Dev && Packages == PackageLocation.DevDrive ? DevDrive.Root : SystemDrive.Root;
}

/// <summary>
/// Runs the plan and emits the timestamped event stream. Order per test mirrors the original
/// methodology: one-time Prepare, then N cold iterations on the system drive, then N on the Dev Drive,
/// then Cleanup (always, in <c>finally</c>). Unavailable/failed tests become skipped rows; only
/// cancellation aborts the run.
/// </summary>
public sealed class BenchRunner
{
    private const double ActivityIntervalSeconds = 0.25;

    private readonly IReadOnlyList<IWorkloadBenchmark> _workloads;
    private readonly IToolProbe _tools;
    private readonly Func<DriveDescriptor, DriveDescriptor, IActivitySource?> _activityFactory;
    private readonly Func<MachineInfo> _machine;
    private readonly Func<string, string?> _filterInfo;
    private readonly Func<string> _seedRootFactory;
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<string, string> _prepared = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _preparedGate = new();

    public BenchRunner(
        IReadOnlyList<IWorkloadBenchmark> workloads,
        IToolProbe tools,
        Func<DriveDescriptor, DriveDescriptor, IActivitySource?>? activityFactory = null,
        Func<MachineInfo>? machine = null,
        Func<string, string?>? filterInfo = null,
        Func<string>? seedRootFactory = null)
    {
        _workloads = workloads ?? throw new ArgumentNullException(nameof(workloads));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _activityFactory = activityFactory ?? ((_, _) => null);
        _machine = machine ?? (() => new MachineInfo());
        _filterInfo = filterInfo ?? (_ => null);
        _seedRootFactory = seedRootFactory ?? (() => Path.Combine(Path.GetTempPath(), WorkloadPaths.SeedFolderName));
    }

    /// <summary>Wires the developer workloads, tool probe, and performance counters.</summary>
    public static BenchRunner CreateDefault()
    {
        var processRunner = new WorkloadProcessRunner();
        return new BenchRunner(
            WorkloadBenchmarkCatalog.CreateDefault(processRunner),
            new ToolProbe(processRunner),
            (system, dev) => new PerfCounterActivitySource(system, dev),
            MachineProbe.Capture,
            root => MachineProbe.DevDriveFilterInfo(root, processRunner));
    }

    /// <summary>Time since the current (or last) run started — the clock every event is stamped with.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    public static string AppVersion =>
        typeof(BenchRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0";

    /// <summary>
    /// Describes every selectable test for <paramref name="profile"/>, probing which workload tools are
    /// installed (runs read-only <c>--version</c> probes, so call it off the UI thread).
    /// </summary>
    public IReadOnlyList<TestDescriptor> DescribeTests(WorkloadProfile profile)
    {
        var all = new List<TestDescriptor>();
        foreach (string group in TestCatalog.GroupOrder)
        {
            IWorkloadBenchmark? workload = _workloads.FirstOrDefault(w => TestCatalog.WorkloadId(w.RequiredTool) == group);
            if (workload is null)
            {
                continue;
            }

            workload.Profile = profile;
            ToolInfo tool = _tools.Detect(workload.RequiredTool);
            all.Add(new TestDescriptor
            {
                Id = group,
                Group = group,
                IsWorkload = true,
                Name = workload.Name,
                Caption = TestCatalog.WorkloadCaption(group, profile),
                Detail = workload.Detail,
                Unit = "s",
                Better = Better.Lower,
                RequiredTool = workload.RequiredTool,
                ToolVersion = tool.Version,
                ToolBinary = tool.Binary,
                ToolArchitecture = tool.Architecture,
                Available = tool.Found,
                UnavailableReason = tool.Found ? null : tool.Path is null ? $"{workload.RequiredTool} not installed" : $"{workload.RequiredTool} installed but not runnable",
                PackageVariable = TestCatalog.PackageVariable(group),
            });
        }

        return all;
    }

    /// <summary>
    /// Runs the plan on a background thread. <paramref name="sink"/> receives every event (on the
    /// benchmark or sampler thread, serialized, in timestamp order) and must return quickly.
    /// Workloads already prepared (by <see cref="PrepareAsync"/> or an earlier run) go straight to
    /// measuring; the rest prepare inline. Prepared seeds are kept for the next run (see
    /// <see cref="ReleasePrepared"/>). The returned recording is complete even when cancelled; it is
    /// returned after the run's per-drive files are deleted, which happens after
    /// <see cref="RunCompletedEvent"/> so the result appears without waiting for cleanup.
    /// </summary>
    public Task<RunRecording> RunAsync(BenchPlan plan, Action<BenchEvent>? sink = null, IReadOnlyList<TestDescriptor>? tests = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => Session(plan, sink, tests, SessionKind.Run, cancellationToken), CancellationToken.None);
    }

    /// <summary>
    /// Prepares every selected workload's fixture ahead of time (the network/seed step) and keeps it,
    /// emitting the same event stream so the preparation can be shown live and replayed. A later
    /// <see cref="RunAsync"/> skips straight to measuring for these workloads.
    /// </summary>
    public Task<RunRecording> PrepareAsync(BenchPlan plan, Action<BenchEvent>? sink = null, IReadOnlyList<TestDescriptor>? tests = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => Session(plan, sink, tests, SessionKind.Prepare, cancellationToken), CancellationToken.None);
    }

    /// <summary>
    /// Identity of a prepared fixture: the tool that built it, for git the fixture size, and for workloads
    /// with a package cache the drives it is staged on. Iterations and the package location don't change
    /// what is prepared (every location is staged), so they don't invalidate a preparation.
    /// </summary>
    public static string PrepareKey(TestDescriptor test, WorkloadProfile profile, string systemRoot, string devRoot) =>
        $"{test.ToolVersion}|{test.ToolBinary}|{(test.Group == TestCatalog.GitClone ? profile.ToString() : string.Empty)}|" +
        (test.PackageVariable is null ? string.Empty : $"{systemRoot.ToUpperInvariant()}|{devRoot.ToUpperInvariant()}");

    /// <summary>True when <paramref name="test"/>'s fixture is prepared for this tool, profile and drives (disk tests need none).</summary>
    public bool IsPrepared(TestDescriptor test, WorkloadProfile profile, string systemRoot, string devRoot)
    {
        if (!test.IsWorkload)
        {
            return true;
        }

        lock (_preparedGate)
        {
            return _prepared.TryGetValue(test.Group, out string? key) && key == PrepareKey(test, profile, systemRoot, devRoot);
        }
    }

    private bool IsPrepared(TestDescriptor test, BenchPlan plan) =>
        IsPrepared(test, plan.Profile, plan.SystemDrive.Root, plan.DevDrive.Root);

    /// <summary>True when any prepared seed is being kept (so it must be released on exit).</summary>
    public bool HasPreparedSeeds
    {
        get
        {
            lock (_preparedGate)
            {
                return _prepared.Count > 0;
            }
        }
    }

    /// <summary>Deletes every kept seed (and anything left on the drives). Safe to call any time; never throws.</summary>
    public void ReleasePrepared()
    {
        lock (_preparedGate)
        {
            _prepared.Clear();
        }

        foreach (IWorkloadBenchmark workload in _workloads)
        {
            try
            {
                workload.Cleanup();
            }
            catch
            {
                // Cleanup is best-effort by contract.
            }
        }
    }

    private RunRecording Session(BenchPlan plan, Action<BenchEvent>? sink, IReadOnlyList<TestDescriptor>? describedTests, SessionKind kind, CancellationToken ct)
    {
        int iterations = Math.Max(1, plan.Iterations);
        var events = new List<BenchEvent>(1024);
        object gate = new();
        bool stopped = false;

        void Emit(BenchEvent e)
        {
            lock (gate)
            {
                if (stopped)
                {
                    return;
                }

                BenchEvent stamped = e with { T = Math.Round(_clock.Elapsed.TotalSeconds, 3) };
                events.Add(stamped);
                stopped = e is RunCompletedEvent;
                sink?.Invoke(stamped);
            }
        }

        _clock.Reset();

        IReadOnlyList<TestDescriptor> described = describedTests ?? DescribeTests(plan.Profile);
        var groups = new HashSet<string>(plan.Groups ?? TestCatalog.GroupOrder, StringComparer.OrdinalIgnoreCase);
        TestDescriptor[] tests = described
            .Where(t => t.IsWorkload && groups.Contains(t.Group) && TestCatalog.GroupOrder.Contains(t.Group))
            .ToArray();

        var info = new RunInfo
        {
            RunId = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.Now,
            AppVersion = AppVersion,
            Machine = _machine(),
            SystemDrive = plan.SystemDrive,
            DevDrive = plan.DevDrive,
            Iterations = iterations,
            Profile = plan.Profile,
            Tests = tests,
            DevDriveFilterInfo = _filterInfo(plan.DevDrive.Root),
            Session = kind,
            PreparedAhead = kind == SessionKind.Run && tests.Any(t => t.IsWorkload && t.Available && IsPrepared(t, plan)),
            Packages = plan.Packages,
        };

        _clock.Restart();
        Emit(new RunStartedEvent(info));

        using var activityStop = new CancellationTokenSource();
        Thread activityThread = StartActivity(plan, Emit, activityStop.Token);

        var touched = new List<IWorkloadBenchmark>();
        RunOutcome outcome = RunOutcome.Completed;
        string? error = null;
        try
        {
            var environment = new WorkloadEnvironment(plan.SystemDrive.Root, plan.DevDrive.Root, _seedRootFactory());
            foreach (string group in TestCatalog.GroupOrder.Where(groups.Contains))
            {
                ct.ThrowIfCancellationRequested();
                TestDescriptor[] rows = tests.Where(t => t.Group == group).ToArray();
                if (rows.Length == 0)
                {
                    continue;
                }

                IWorkloadBenchmark? benchmark = _workloads.FirstOrDefault(w => TestCatalog.WorkloadId(w.RequiredTool) == group);
                if (benchmark is not null)
                {
                    touched.Add(benchmark);
                }

                if (kind == SessionKind.Prepare)
                {
                    PrepareWorkload(rows[0], benchmark, plan, environment, Emit, ct);
                }
                else
                {
                    RunWorkload(rows[0], benchmark, plan, environment, iterations, Emit, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            outcome = RunOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            outcome = RunOutcome.Failed;
            error = ex.Message;
        }
        finally
        {
            activityStop.Cancel();
            activityThread.Join(TimeSpan.FromSeconds(2));
        }

        Emit(new RunCompletedEvent(outcome, error));
        _clock.Stop();

        // Off the measured path: re-stage the package caches the run read (in parallel; they are independent
        // trees) while deleting what it wrote on the drives (working copies), so the next run starts from
        // freshly staged caches. Seeds stay for the next run until ReleasePrepared. (Prepare writes nothing there.)
        if (kind == SessionKind.Run && touched.Count > 0)
        {
            Task restage = Task.Run(() => Parallel.ForEach(touched, benchmark =>
            {
                try
                {
                    benchmark.RefreshPackages();
                }
                catch
                {
                    // Best-effort by contract.
                }
            }));

            foreach (IWorkloadBenchmark benchmark in touched)
            {
                try
                {
                    benchmark.CleanupDrives();
                }
                catch
                {
                    // Best-effort by contract.
                }
            }

            restage.Wait();
        }

        BenchEvent[] snapshot;
        lock (gate)
        {
            snapshot = events.ToArray();
        }

        return RunRecording.FromEvents(snapshot);
    }

    private void PrepareWorkload(TestDescriptor test, IWorkloadBenchmark? benchmark, BenchPlan plan, WorkloadEnvironment environment, Action<BenchEvent> emit, CancellationToken ct)
    {
        emit(new TestStartedEvent(test.Id));
        if (benchmark is null || !test.Available)
        {
            emit(new TestSkippedEvent(test.Id, test.UnavailableReason ?? "not available"));
            return;
        }

        if (IsPrepared(test, plan))
        {
            emit(new TestPreparedEvent(test.Id, 0, Reused: true));
            return;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            EnsurePrepared(test, benchmark, plan, environment, emit, ct);
            emit(new TestPreparedEvent(test.Id, Math.Round(stopwatch.Elapsed.TotalSeconds, 2)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkloadUnavailableException ex)
        {
            emit(new TestSkippedEvent(test.Id, ex.Message));
        }
        catch (Exception ex)
        {
            emit(new TestSkippedEvent(test.Id, "could not prepare: " + ex.Message));
        }
    }

    /// <summary>
    /// Prepares <paramref name="benchmark"/> unless its kept seed still matches; a stale seed (different
    /// tool or fixture size) is deleted first. On any failure the partial seed is deleted.
    /// </summary>
    private void EnsurePrepared(TestDescriptor test, IWorkloadBenchmark benchmark, BenchPlan plan, WorkloadEnvironment environment, Action<BenchEvent> emit, CancellationToken ct)
    {
        benchmark.Profile = plan.Profile;
        if (IsPrepared(test, plan))
        {
            return;
        }

        Forget(test.Group, benchmark);
        emit(new PhaseStartedEvent(test.Id, RunPhase.Prepare, DriveRole.None, 0));
        try
        {
            benchmark.Prepare(environment, ct);
        }
        catch
        {
            benchmark.Cleanup();
            throw;
        }

        lock (_preparedGate)
        {
            _prepared[test.Group] = PrepareKey(test, plan.Profile, plan.SystemDrive.Root, plan.DevDrive.Root);
        }
    }

    private void Forget(string group, IWorkloadBenchmark benchmark)
    {
        bool had;
        lock (_preparedGate)
        {
            had = _prepared.Remove(group);
        }

        if (had)
        {
            benchmark.Cleanup();
        }
    }

    private void RunWorkload(TestDescriptor test, IWorkloadBenchmark? benchmark, BenchPlan plan, WorkloadEnvironment environment, int iterations, Action<BenchEvent> emit, CancellationToken ct)
    {
        emit(new TestStartedEvent(test.Id));
        if (benchmark is null || !test.Available)
        {
            emit(new TestSkippedEvent(test.Id, test.UnavailableReason ?? "not available"));
            return;
        }

        bool completed = false;
        try
        {
            EnsurePrepared(test, benchmark, plan, environment, emit, ct);

            foreach ((DriveRole role, string root) in new[] { (DriveRole.System, plan.SystemDrive.Root), (DriveRole.Dev, plan.DevDrive.Root) })
            {
                for (int i = 1; i <= iterations; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int iteration = i;
                    bool reported = false;
                    emit(new PhaseStartedEvent(test.Id, RunPhase.Setup, role, iteration));
                    benchmark.TimedSectionStarting = () => emit(new PhaseStartedEvent(test.Id, RunPhase.Measure, role, iteration));
                    benchmark.TimedSectionCompleted = seconds =>
                    {
                        reported = true;
                        emit(new MeasurementEvent(test.Id, role, iteration, Math.Round(seconds, 4)));
                    };
                    double measured = benchmark.MeasureOnce(root, plan.PackagesRoot(role), ct);
                    if (!reported)
                    {
                        emit(new MeasurementEvent(test.Id, role, iteration, Math.Round(measured, 4)));
                    }
                }
            }

            completed = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkloadUnavailableException ex)
        {
            emit(new TestSkippedEvent(test.Id, ex.Message));
            Forget(test.Group, benchmark);
        }
        catch (Exception ex)
        {
            emit(new TestSkippedEvent(test.Id, "could not run: " + ex.Message));
            Forget(test.Group, benchmark);
        }
        finally
        {
            benchmark.TimedSectionStarting = null;
            benchmark.TimedSectionCompleted = null;
        }

        if (completed)
        {
            emit(new TestCompletedEvent(test.Id));
        }
    }

    /// <summary>
    /// Samples activity on a dedicated background thread. Counter creation can take seconds the first
    /// time in a process, so it happens here rather than delaying the run; samples simply start later.
    /// </summary>
    private Thread StartActivity(BenchPlan plan, Action<BenchEvent> emit, CancellationToken stop)
    {
        var thread = new Thread(() =>
        {
            using IActivitySource? source = SafeCreateActivity(plan);
            if (source is null)
            {
                return;
            }

            while (!stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(ActivityIntervalSeconds)))
            {
                if (source.Sample() is MachineActivityEvent a)
                {
                    emit(a);
                }
            }
        })
        {
            IsBackground = true,
            Name = "DevDriveBench activity sampler",
        };
        thread.Start();
        return thread;
    }

    private IActivitySource? SafeCreateActivity(BenchPlan plan)
    {
        try
        {
            return _activityFactory(plan.SystemDrive, plan.DevDrive);
        }
        catch
        {
            return null;
        }
    }

}
