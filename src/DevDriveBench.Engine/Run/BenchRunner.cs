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

    private readonly IDiskBenchmark _disk;
    private readonly IReadOnlyList<IWorkloadBenchmark> _workloads;
    private readonly IToolProbe _tools;
    private readonly Func<DriveDescriptor, DriveDescriptor, IActivitySource?> _activityFactory;
    private readonly Func<MachineInfo> _machine;
    private readonly Func<string, string?> _filterInfo;
    private readonly DiskBenchmarkOptions _diskOptions;
    private readonly Func<string> _seedRootFactory;
    private readonly Stopwatch _clock = new();

    public BenchRunner(
        IDiskBenchmark disk,
        IReadOnlyList<IWorkloadBenchmark> workloads,
        IToolProbe tools,
        Func<DriveDescriptor, DriveDescriptor, IActivitySource?>? activityFactory = null,
        Func<MachineInfo>? machine = null,
        Func<string, string?>? filterInfo = null,
        DiskBenchmarkOptions? diskOptions = null,
        Func<string>? seedRootFactory = null)
    {
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
        _workloads = workloads ?? throw new ArgumentNullException(nameof(workloads));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _activityFactory = activityFactory ?? ((_, _) => null);
        _machine = machine ?? (() => new MachineInfo());
        _filterInfo = filterInfo ?? (_ => null);
        _diskOptions = diskOptions ?? DiskBenchmarkOptions.Default;
        _seedRootFactory = seedRootFactory ?? (() => Path.Combine(Path.GetTempPath(), WorkloadPaths.SeedFolderName));
    }

    /// <summary>Wires the real disk benchmark, workloads, tool probe, and performance counters.</summary>
    public static BenchRunner CreateDefault()
    {
        var processRunner = new WorkloadProcessRunner();
        return new BenchRunner(
            new DiskBenchmark(),
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
            if (group == TestCatalog.DiskGroup)
            {
                all.AddRange(TestCatalog.DiskTests(_diskOptions));
                continue;
            }

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
            });
        }

        return all;
    }

    /// <summary>
    /// Runs the plan on a background thread. <paramref name="sink"/> receives every event (on the
    /// benchmark or sampler thread, serialized, in timestamp order) and must return quickly.
    /// The returned recording is complete even when the run is cancelled.
    /// </summary>
    public Task<RunRecording> RunAsync(BenchPlan plan, Action<BenchEvent>? sink = null, IReadOnlyList<TestDescriptor>? tests = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => Run(plan, sink, tests, cancellationToken), CancellationToken.None);
    }

    private RunRecording Run(BenchPlan plan, Action<BenchEvent>? sink, IReadOnlyList<TestDescriptor>? describedTests, CancellationToken ct)
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
        TestDescriptor[] tests = described.Where(t => groups.Contains(t.Group)).ToArray();

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
        };

        _clock.Restart();
        Emit(new RunStartedEvent(info));

        using var activityStop = new CancellationTokenSource();
        Thread activityThread = StartActivity(plan, Emit, activityStop.Token);

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

                if (group == TestCatalog.DiskGroup)
                {
                    RunDisk(rows, plan, iterations, Emit, ct);
                }
                else
                {
                    RunWorkload(rows[0], plan, environment, iterations, Emit, ct);
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

        BenchEvent[] snapshot;
        lock (gate)
        {
            snapshot = events.ToArray();
        }

        return RunRecording.FromEvents(snapshot);
    }

    private void RunWorkload(TestDescriptor test, BenchPlan plan, WorkloadEnvironment environment, int iterations, Action<BenchEvent> emit, CancellationToken ct)
    {
        emit(new TestStartedEvent(test.Id));
        IWorkloadBenchmark? benchmark = _workloads.FirstOrDefault(w => TestCatalog.WorkloadId(w.RequiredTool) == test.Id);
        if (benchmark is null || !test.Available)
        {
            emit(new TestSkippedEvent(test.Id, test.UnavailableReason ?? "not available"));
            return;
        }

        benchmark.Profile = plan.Profile;
        bool completed = false;
        try
        {
            emit(new PhaseStartedEvent(test.Id, RunPhase.Prepare, DriveRole.None, 0));
            benchmark.Prepare(environment, ct);

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
                        emit(new PhaseStartedEvent(test.Id, RunPhase.Cleanup, role, iteration));
                    };
                    double measured = benchmark.MeasureOnce(root, ct);
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
        }
        catch (Exception ex)
        {
            emit(new TestSkippedEvent(test.Id, "could not run: " + ex.Message));
        }
        finally
        {
            benchmark.TimedSectionStarting = null;
            benchmark.TimedSectionCompleted = null;
            if (completed)
            {
                emit(new PhaseStartedEvent(test.Id, RunPhase.Cleanup, DriveRole.None, 0));
            }

            benchmark.Cleanup();
        }

        if (completed)
        {
            emit(new TestCompletedEvent(test.Id));
        }
    }

    private void RunDisk(TestDescriptor[] rows, BenchPlan plan, int iterations, Action<BenchEvent> emit, CancellationToken ct)
    {
        foreach (TestDescriptor row in rows)
        {
            emit(new TestStartedEvent(row.Id));
        }

        var wanted = new HashSet<string>(rows.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach ((DriveRole role, string root) in new[] { (DriveRole.System, plan.SystemDrive.Root), (DriveRole.Dev, plan.DevDrive.Root) })
            {
                for (int i = 1; i <= iterations; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    _disk.Run(root, _diskOptions, new DiskObserver(emit, wanted, role, i), ct);
                }
            }

            foreach (TestDescriptor row in rows)
            {
                emit(new TestCompletedEvent(row.Id));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            foreach (TestDescriptor row in rows)
            {
                emit(new TestSkippedEvent(row.Id, ex.Message));
            }
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

    private sealed class DiskObserver(Action<BenchEvent> emit, HashSet<string> wanted, DriveRole role, int iteration) : IDiskBenchmarkObserver
    {
        public void MetricStarted(DiskMetric metric)
        {
            string id = TestCatalog.DiskId(metric);
            if (wanted.Contains(id))
            {
                emit(new PhaseStartedEvent(id, RunPhase.Measure, role, iteration));
            }
        }

        public void Sample(DiskMetric metric, double fraction, double value)
        {
            string id = TestCatalog.DiskId(metric);
            if (wanted.Contains(id))
            {
                emit(new SampleEvent(id, role, iteration, Math.Round(fraction, 3), Math.Round(value, 1)));
            }
        }

        public void MetricCompleted(DiskMetric metric, double value)
        {
            string id = TestCatalog.DiskId(metric);
            if (wanted.Contains(id))
            {
                emit(new MeasurementEvent(id, role, iteration, Math.Round(value, 2)));
            }
        }
    }
}
