using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Run;

namespace DevDriveBench.Engine.Tests;

internal static class Sample
{
    public static RunInfo Info(int iterations = 1) => new()
    {
        RunId = "test",
        StartedAt = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero),
        Machine = new MachineInfo { Name = "BENCH", Cpu = "Test CPU, 8 cores", Os = "Windows", OsArchitecture = "arm64" },
        SystemDrive = new DriveDescriptor { Root = @"C:\", FileSystem = "NTFS", IsSystem = true },
        DevDrive = new DriveDescriptor { Root = @"D:\", FileSystem = "ReFS", IsDevDrive = true, BusType = "File Backed Virtual", BackingFile = @"C:\dev.vhdx" },
        Iterations = iterations,
        Tests = new[]
        {
            new TestDescriptor { Id = "git-clone", Group = "git-clone", IsWorkload = true, Name = "git clone", Unit = "s", Better = Better.Lower, RequiredTool = "git", ToolVersion = "2.56.0", ToolArchitecture = "x64" },
            new TestDescriptor { Id = "npm-ci", Group = "npm-ci", IsWorkload = true, Name = "npm ci", Unit = "s", Better = Better.Lower },
            new TestDescriptor { Id = "disk-seq-read", Group = "disk", Name = "Sequential read", Unit = "MB/s", Better = Better.Higher },
        },
    };

    /// <summary>git clone: C 8 s, D 2 s (4×); npm skipped; disk read 1000 vs 3000 MB/s (3×).</summary>
    public static List<BenchEvent> Stream() => new()
    {
        new RunStartedEvent(Info()) { T = 0 },
        new TestStartedEvent("git-clone") { T = 0.01 },
        new PhaseStartedEvent("git-clone", RunPhase.Prepare, DriveRole.None, 0) { T = 0.02 },
        new PhaseStartedEvent("git-clone", RunPhase.Setup, DriveRole.System, 1) { T = 1 },
        new PhaseStartedEvent("git-clone", RunPhase.Measure, DriveRole.System, 1) { T = 1.5 },
        new MeasurementEvent("git-clone", DriveRole.System, 1, 8) { T = 9.5 },
        new PhaseStartedEvent("git-clone", RunPhase.Measure, DriveRole.Dev, 1) { T = 10 },
        new MachineActivityEvent(12.5, 300, 40) { T = 10.25 },
        new MeasurementEvent("git-clone", DriveRole.Dev, 1, 2) { T = 12 },
        new TestCompletedEvent("git-clone") { T = 12.5 },
        new TestStartedEvent("npm-ci") { T = 12.6 },
        new TestSkippedEvent("npm-ci", "npm not installed") { T = 12.6 },
        new TestStartedEvent("disk-seq-read") { T = 13 },
        new PhaseStartedEvent("disk-seq-read", RunPhase.Measure, DriveRole.System, 1) { T = 13 },
        new SampleEvent("disk-seq-read", DriveRole.System, 1, 0.5, 950) { T = 13.1 },
        new MeasurementEvent("disk-seq-read", DriveRole.System, 1, 1000) { T = 13.2 },
        new PhaseStartedEvent("disk-seq-read", RunPhase.Measure, DriveRole.Dev, 1) { T = 13.3 },
        new MeasurementEvent("disk-seq-read", DriveRole.Dev, 1, 3000) { T = 13.4 },
        new TestCompletedEvent("disk-seq-read") { T = 13.5 },
        new RunCompletedEvent(RunOutcome.Completed) { T = 14 },
    };
}

public class RunStateTests
{
    [Fact]
    public void FoldsResultsAndHeadlineFromWorkloadsOnly()
    {
        var state = new RunState();
        foreach (BenchEvent e in Sample.Stream())
        {
            state.Apply(e);
        }

        TestState git = state.Find("git-clone")!;
        Assert.Equal(TestStatus.Done, git.Status);
        Assert.Equal(8, git.SystemValue);
        Assert.Equal(2, git.DevValue);
        Assert.Equal(4, git.Advantage!.Value, 6);

        Assert.Equal(TestStatus.Skipped, state.Find("npm-ci")!.Status);
        Assert.Equal("npm not installed", state.Find("npm-ci")!.SkipReason);

        Assert.Equal(3, state.Find("disk-seq-read")!.Advantage!.Value, 6);

        // Headline is the median of WORKLOAD advantages only: the 3× disk row must not count.
        Assert.Equal(4, state.Headline!.Value, 6);
        Assert.Equal(1, state.HeadlineCount);
        Assert.Equal(RunOutcome.Completed, state.Outcome);
        Assert.Single(state.Activity);
    }

    [Fact]
    public void MeasurementClosesTheTimedSpan()
    {
        var state = new RunState();
        foreach (BenchEvent e in Sample.Stream())
        {
            state.Apply(e);
        }

        PhaseSpan measure = state.Timeline.First(s => s.TestId == "git-clone" && s.Phase == RunPhase.Measure && s.Drive == DriveRole.System);
        Assert.Equal(1.5, measure.Start);
        Assert.Equal(9.5, measure.End);
        Assert.All(state.Timeline, s => Assert.True(s.End >= s.Start));
    }

    [Fact]
    public void ActiveStateIsTrackedWhileMeasuring()
    {
        var state = new RunState();
        foreach (BenchEvent e in Sample.Stream().TakeWhile(e => e is not MeasurementEvent))
        {
            state.Apply(e);
        }

        TestState git = state.Find("git-clone")!;
        Assert.Equal(TestStatus.Running, git.Status);
        Assert.Equal(DriveRole.System, git.ActiveDrive);
        Assert.Equal(RunPhase.Measure, git.ActivePhase);
        Assert.Equal(1.5, git.ActivePhaseStart);
        Assert.Null(state.Headline);
    }

    [Fact]
    public void RunStartedResetsState()
    {
        var state = new RunState();
        foreach (BenchEvent e in Sample.Stream())
        {
            state.Apply(e);
        }

        state.Apply(new RunStartedEvent(Sample.Info()));
        Assert.Null(state.Outcome);
        Assert.All(state.Tests, t => Assert.Equal(TestStatus.Pending, t.Status));
        Assert.Empty(state.Timeline);
    }
}
