using System.Text.RegularExpressions;
using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Platform;
using DevDriveBench.Engine.Run;

namespace DevDriveBench.Engine.Tests;

public class BenchRunnerTests
{
    private static readonly BenchPlan Plan = new()
    {
        SystemDrive = new DriveDescriptor { Root = @"C:\", FileSystem = "NTFS", IsSystem = true },
        DevDrive = new DriveDescriptor { Root = @"D:\", FileSystem = "ReFS", IsDevDrive = true },
        Iterations = 1,
    };

    [Fact]
    public async Task DefaultSingleRunProducesOneMeasurementPerDrive()
    {
        var git = new FakeWorkload("git", system: 6, dev: 2);
        var runner = Create(git);
        var seen = new List<BenchEvent>();

        RunRecording r = await runner.RunAsync(Plan with { Groups = new[] { TestCatalog.GitClone } }, seen.Add);

        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.IsType<RunStartedEvent>(r.Events[0]);
        Assert.IsType<RunCompletedEvent>(r.Events[^1]);
        Assert.Equal(r.Events.Count, seen.Count);
        Assert.Equal(2, r.Events.OfType<MeasurementEvent>().Count());
        TestResult result = Assert.Single(r.Results);
        Assert.Equal(3, result.Advantage!.Value, 6);
        Assert.Equal(3, r.Headline!.Value, 6);
        Assert.Equal(1, git.Prepared);
        Assert.Equal(0, git.CleanedUp); // seed kept for the next run
        Assert.Equal(1, git.CleanedUpDrives);
    }

    [Fact]
    public async Task IterationsRunSystemThenDevAndDiscardFirst()
    {
        var git = new FakeWorkload("git", systemRuns: new[] { 20.0, 6, 6 }, devRuns: new[] { 10.0, 2, 2 });
        RunRecording r = await Create(git).RunAsync(Plan with { Iterations = 3, Groups = new[] { TestCatalog.GitClone } });

        MeasurementEvent[] m = r.Events.OfType<MeasurementEvent>().ToArray();
        Assert.Equal(6, m.Length);
        Assert.All(m.Take(3), e => Assert.Equal(DriveRole.System, e.Drive));
        Assert.All(m.Skip(3), e => Assert.Equal(DriveRole.Dev, e.Drive));
        Assert.Equal(new[] { 1, 2, 3 }, m.Take(3).Select(e => e.Iteration));

        TestResult result = Assert.Single(r.Results);
        Assert.Equal(6, result.SystemValue);
        Assert.Equal(2, result.DevValue);
        Assert.Equal(new[] { 20.0, 6, 6 }, result.SystemRuns);
    }

    [Fact]
    public async Task MissingToolBecomesSkippedRowWithoutRunning()
    {
        var npm = new FakeWorkload("npm", 1, 1);
        RunRecording r = await Create(npm, missingTools: new[] { "npm" }).RunAsync(Plan with { Groups = new[] { TestCatalog.NpmCi } });

        TestSkippedEvent skipped = Assert.Single(r.Events.OfType<TestSkippedEvent>());
        Assert.Equal("npm not installed", skipped.Reason);
        Assert.Equal(0, npm.Prepared);
        Assert.Null(r.Headline);
    }

    [Fact]
    public async Task UnavailableDuringPrepareSkipsAndStillCleansUp()
    {
        var dotnet = new FakeWorkload("dotnet", 1, 1) { PrepareThrows = new WorkloadUnavailableException("no network") };
        var git = new FakeWorkload("git", 4, 2);
        RunRecording r = await Create(git, dotnet).RunAsync(Plan with { Groups = new[] { TestCatalog.GitClone, TestCatalog.DotnetBuild } });

        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Equal("no network", r.Events.OfType<TestSkippedEvent>().Single().Reason);
        Assert.Equal(1, dotnet.CleanedUp);
        Assert.Equal(2, r.Headline!.Value, 6);
    }

    [Fact]
    public async Task CancellationEndsRunAndCleansUp()
    {
        using var cts = new CancellationTokenSource();
        var git = new FakeWorkload("git", 4, 2) { OnMeasure = cts.Cancel };
        RunRecording r = await Create(git).RunAsync(Plan with { Iterations = 3, Groups = new[] { TestCatalog.GitClone } }, cancellationToken: cts.Token);

        Assert.Equal(RunOutcome.Cancelled, r.Outcome);
        Assert.Equal(RunOutcome.Cancelled, ((RunCompletedEvent)r.Events[^1]).Outcome);
        Assert.Equal(1, git.CleanedUpDrives);
    }

    [Fact]
    public async Task DiskObserverEventsMapToRows()
    {
        RunRecording r = await Create().RunAsync(Plan with { Iterations = 2, Groups = new[] { TestCatalog.DiskGroup } });

        Assert.Equal(4, r.Results.Count);
        Assert.Equal(4 * 2 * 2, r.Events.OfType<MeasurementEvent>().Count());
        Assert.Contains(r.Events, e => e is SampleEvent s && s.TestId == TestCatalog.DiskSeqRead);
        TestResult read = r.Results.Single(x => x.TestId == TestCatalog.DiskSeqRead);
        Assert.Equal(2, read.Advantage!.Value, 6);
        Assert.Null(r.Headline);
    }

    [Fact]
    public async Task EventTimesAreMonotonic()
    {
        RunRecording r = await Create(new FakeWorkload("git", 0.01, 0.01)).RunAsync(Plan with { Iterations = 2 });
        for (int i = 1; i < r.Events.Count; i++)
        {
            Assert.True(r.Events[i].T >= r.Events[i - 1].T);
        }
    }

    [Fact]
    public async Task PrepareAheadThenRunsGoStraightToMeasuring()
    {
        var git = new FakeWorkload("git", 4, 2);
        var runner = Create(git);
        BenchPlan plan = Plan with { Groups = new[] { TestCatalog.GitClone, TestCatalog.DiskGroup } };

        RunRecording prep = await runner.PrepareAsync(plan);
        Assert.Equal(SessionKind.Prepare, prep.Info.Session);
        Assert.Single(prep.Events.OfType<PhaseStartedEvent>(), p => p.Phase == RunPhase.Prepare);
        Assert.Contains(prep.Events, e => e is TestPreparedEvent { TestId: TestCatalog.GitClone, Reused: false });
        Assert.Equal(4, prep.Events.OfType<TestPreparedEvent>().Count(e => e.TestId.StartsWith("disk", StringComparison.Ordinal)));
        Assert.Empty(prep.Events.OfType<MeasurementEvent>());
        Assert.True(runner.HasPreparedSeeds);

        RunState state = Fold(prep.Events);
        Assert.All(state.Tests, t => Assert.Equal(TestStatus.Ready, t.Status));

        RunRecording first = await runner.RunAsync(plan);
        RunRecording second = await runner.RunAsync(plan);
        foreach (RunRecording run in new[] { first, second })
        {
            Assert.True(run.Info.PreparedAhead);
            Assert.DoesNotContain(run.Events, e => e is PhaseStartedEvent { Phase: RunPhase.Prepare });
            Assert.Equal(2, run.Events.OfType<MeasurementEvent>().Count(m => m.TestId == TestCatalog.GitClone));
        }

        Assert.Equal(1, git.Prepared);
        Assert.Equal(0, git.CleanedUp);
        Assert.Equal(2, git.CleanedUpDrives);

        // A second Prepare reuses what is already there.
        RunRecording again = await runner.PrepareAsync(plan);
        Assert.Contains(again.Events, e => e is TestPreparedEvent { TestId: TestCatalog.GitClone, Reused: true });
        Assert.Equal(1, git.Prepared);

        runner.ReleasePrepared();
        Assert.False(runner.HasPreparedSeeds);
        Assert.Equal(1, git.CleanedUp);
    }

    [Fact]
    public async Task GitFixtureSizeAndToolChangesInvalidateThePreparation()
    {
        var git = new FakeWorkload("git", 4, 2);
        var dotnet = new FakeWorkload("dotnet", 4, 2);
        var runner = Create(git, dotnet);
        BenchPlan plan = Plan with { Groups = new[] { TestCatalog.GitClone, TestCatalog.DotnetBuild }, Profile = WorkloadProfile.Thorough };
        await runner.PrepareAsync(plan);

        // Quick git fixture is a different seed; dotnet is unaffected by the profile.
        RunRecording quick = await runner.RunAsync(plan with { Profile = WorkloadProfile.Quick });
        Assert.Equal(2, git.Prepared);
        Assert.Equal(1, git.CleanedUp);
        Assert.Equal(1, dotnet.Prepared);
        Assert.Single(quick.Events.OfType<PhaseStartedEvent>(), p => p.Phase == RunPhase.Prepare && p.TestId == TestCatalog.GitClone);

        // A different dotnet SDK means a different seed.
        IReadOnlyList<TestDescriptor> newSdk = runner.DescribeTests(WorkloadProfile.Quick)
            .Select(t => t.Group == TestCatalog.DotnetBuild ? t with { ToolVersion = "11.0.100" } : t)
            .ToArray();
        Assert.False(runner.IsPrepared(newSdk.Single(t => t.Group == TestCatalog.DotnetBuild), WorkloadProfile.Quick, Plan.SystemDrive.Root, Plan.DevDrive.Root));
        await runner.RunAsync(plan with { Profile = WorkloadProfile.Quick }, tests: newSdk);
        Assert.Equal(2, dotnet.Prepared);
    }

    [Fact]
    public async Task PackageLocationRoutesOnlyTheDevDriveRunsPackages()
    {
        var npm = new FakeWorkload("npm", 4, 2);
        var runner = Create(npm);
        BenchPlan plan = Plan with { Groups = new[] { TestCatalog.NpmCi } };

        RunRecording onDev = await runner.RunAsync(plan);
        RunRecording onSystem = await runner.RunAsync(plan with { Packages = PackageLocation.SystemDrive });

        Assert.Equal(new[] { (@"C:\", @"C:\"), (@"D:\", @"D:\"), (@"C:\", @"C:\"), (@"D:\", @"C:\") }, npm.Measured);
        Assert.Equal(PackageLocation.DevDrive, onDev.Info.Packages);
        Assert.Equal(PackageLocation.SystemDrive, onSystem.Info.Packages);
        Assert.Equal("npm_config_cache", onSystem.Info.Tests.Single().PackageVariable);

        // Both locations are staged by one Prepare; every run re-stages what it read, after the result.
        Assert.Equal(1, npm.Prepared);
        Assert.Equal(2, npm.Refreshed);
        Assert.IsType<RunCompletedEvent>(onSystem.Events[^1]);
    }

    [Fact]
    public async Task ChangingTheDevDriveInvalidatesStagedPackagesButNotGit()
    {
        var git = new FakeWorkload("git", 4, 2);
        var cargo = new FakeWorkload("cargo", 4, 2);
        var runner = Create(git, cargo);
        BenchPlan plan = Plan with { Groups = new[] { TestCatalog.GitClone, TestCatalog.CargoBuild } };
        await runner.PrepareAsync(plan);

        BenchPlan otherDevDrive = plan with { DevDrive = plan.DevDrive with { Root = @"E:\" } };
        await runner.RunAsync(otherDevDrive);

        Assert.Equal(1, git.Prepared);
        Assert.Equal(2, cargo.Prepared);
    }

    [Fact]
    public async Task FailedPrepareIsNotKept()
    {
        var dotnet = new FakeWorkload("dotnet", 1, 1) { PrepareThrows = new WorkloadUnavailableException("no network") };
        var runner = Create(dotnet);
        RunRecording prep = await runner.PrepareAsync(Plan with { Groups = new[] { TestCatalog.DotnetBuild } });

        Assert.Equal("no network", prep.Events.OfType<TestSkippedEvent>().Single().Reason);
        Assert.False(runner.HasPreparedSeeds);
        Assert.Equal(1, dotnet.CleanedUp);
    }

    [Fact]
    public async Task StageTimingSeparatesMeasuredTimeFromGaps()
    {
        var git = new FakeWorkload("git", 0.05, 0.05);
        RunRecording r = await Create(git).RunAsync(Plan with { Groups = new[] { TestCatalog.GitClone } });
        StageTiming timing = StageTiming.From(r.Events);

        Assert.Equal(2, timing.Spans.Count);
        Assert.Equal(timing.Spans.Count + 1, timing.Gaps.Count);
        Assert.Equal("start", timing.Gaps[0].After);
        Assert.Equal("end", timing.Gaps[^1].Before);
        Assert.InRange(timing.Measured, 0, timing.Total);
        Assert.Equal(timing.Total - timing.Measured, timing.DeadTime, 6);
    }

    private static RunState Fold(IEnumerable<BenchEvent> events)
    {
        var state = new RunState();
        foreach (BenchEvent e in events)
        {
            state.Apply(e);
        }

        return state;
    }

    [Fact]
    public void FastImportStreamDescribesEveryFile()
    {
        string stream = GitCloneWorkload.BuildFastImportStream(3, 4, CancellationToken.None);
        Assert.StartsWith("commit refs/heads/main\n", stream);
        Assert.Equal(3 * 4 + 2, Regex.Matches(stream, "^M 100644 inline ", RegexOptions.Multiline).Count);

        // Every "data N" header must be followed by exactly N bytes and a newline.
        int pos = 0;
        var header = new Regex(@"data (\d+)\n");
        while (header.Match(stream, pos) is { Success: true } match)
        {
            int length = int.Parse(match.Groups[1].Value);
            int start = match.Index + match.Length;
            Assert.Equal('\n', stream[start + length]);
            pos = start + length;
        }
    }

    private static BenchRunner Create(params FakeWorkload[] workloads) => Create(workloads, Array.Empty<string>());

    private static BenchRunner Create(FakeWorkload workload, string[] missingTools) => Create(new[] { workload }, missingTools);

    private static BenchRunner Create(FakeWorkload[] workloads, string[] missingTools) =>
        new(new FakeDisk(), workloads, new FakeTools(missingTools), seedRootFactory: () => Path.GetTempPath());

    private sealed class FakeTools(string[] missing) : IToolProbe
    {
        public ToolInfo Detect(string tool) => new(tool, !missing.Contains(tool), "1.0", null);
    }

    private sealed class FakeDisk : IDiskBenchmark
    {
        public DiskBenchmarkResult Run(string driveRoot, DiskBenchmarkOptions options, IDiskBenchmarkObserver? observer = null, CancellationToken cancellationToken = default)
        {
            double scale = driveRoot.StartsWith("D", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            foreach (DiskMetric metric in Enum.GetValues<DiskMetric>())
            {
                observer?.MetricStarted(metric);
                observer?.Sample(metric, 0.5, 100 * scale);
                observer?.MetricCompleted(metric, 100 * scale);
            }

            return new DiskBenchmarkResult { DriveRoot = driveRoot };
        }
    }

    private sealed class FakeWorkload : IWorkloadBenchmark
    {
        private readonly Queue<double> _system;
        private readonly Queue<double> _dev;

        public FakeWorkload(string tool, double system, double dev)
            : this(tool, Enumerable.Repeat(system, 10).ToArray(), Enumerable.Repeat(dev, 10).ToArray())
        {
        }

        public FakeWorkload(string tool, double[] systemRuns, double[] devRuns)
        {
            RequiredTool = tool;
            _system = new Queue<double>(systemRuns);
            _dev = new Queue<double>(devRuns);
        }

        public string Name => RequiredTool + " workload";

        public string Detail => "fake";

        public WorkloadProfile Profile { get; set; }

        public string RequiredTool { get; }

        public Action? TimedSectionStarting { get; set; }

        public Action<double>? TimedSectionCompleted { get; set; }

        public Exception? PrepareThrows { get; init; }

        public Action? OnMeasure { get; init; }

        public int Prepared { get; private set; }

        public int CleanedUp { get; private set; }

        public void Prepare(WorkloadEnvironment environment, CancellationToken cancellationToken)
        {
            Prepared++;
            if (PrepareThrows is not null)
            {
                throw PrepareThrows;
            }
        }

        public double MeasureOnce(string driveRoot, string packagesRoot, CancellationToken cancellationToken)
        {
            OnMeasure?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            Measured.Add((driveRoot, packagesRoot));
            TimedSectionStarting?.Invoke();
            double value = driveRoot.StartsWith("D", StringComparison.OrdinalIgnoreCase) ? _dev.Dequeue() : _system.Dequeue();
            TimedSectionCompleted?.Invoke(value);
            return value;
        }

        public List<(string Code, string Packages)> Measured { get; } = new();

        public int CleanedUpDrives { get; private set; }

        public int Refreshed { get; private set; }

        public void RefreshPackages() => Refreshed++;

        public void CleanupDrives() => CleanedUpDrives++;

        public void Cleanup() => CleanedUp++;
    }
}
