using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Run;

namespace DevDriveBench.Engine.Tests;

public class RecordingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ddb-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void FromEventsBuildsSummary()
    {
        RunRecording r = RunRecording.FromEvents(Sample.Stream());
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Equal(14, r.DurationSeconds);
        Assert.Equal(4, r.Headline!.Value, 6);
        Assert.Equal(3, r.Results.Count);
    }

    [Fact]
    public void PreparationIsSavedWithTheRun()
    {
        var prepEvents = new List<BenchEvent>
        {
            new RunStartedEvent(Sample.Info() with { Session = SessionKind.Prepare }) { T = 0 },
            new TestStartedEvent("git-clone") { T = 0 },
            new PhaseStartedEvent("git-clone", RunPhase.Prepare, DriveRole.None, 0) { T = 0.1 },
            new TestPreparedEvent("git-clone", 0.9) { T = 1.0 },
            new RunCompletedEvent(RunOutcome.Completed) { T = 1.1 },
        };
        RunRecording run = RunRecording.FromEvents(Sample.Stream()) with { Preparation = RunRecording.FromEvents(prepEvents) };

        RunRecording loaded = RecordingStore.Load(RecordingStore.Save(run, _dir));
        Assert.NotNull(loaded.Preparation);
        Assert.Equal(SessionKind.Prepare, loaded.Preparation!.Info.Session);
        Assert.Equal(0.9, loaded.Preparation.Events.OfType<TestPreparedEvent>().Single().Seconds);
    }

    [Fact]
    public void JsonRoundTripPreservesEveryEvent()
    {
        RunRecording original = RunRecording.FromEvents(Sample.Stream());
        string path = RecordingStore.Save(original, _dir);
        RunRecording loaded = RecordingStore.Load(path);

        Assert.Equal(original.Events.Count, loaded.Events.Count);
        for (int i = 0; i < original.Events.Count; i++)
        {
            Assert.Equal(original.Events[i].GetType(), loaded.Events[i].GetType());
            Assert.Equal(original.Events[i].T, loaded.Events[i].T);
        }

        var started = Assert.IsType<RunStartedEvent>(loaded.Events[0]);
        Assert.Equal(@"D:\", started.Info.DevDrive.Root);
        Assert.True(started.Info.DevDrive.IsVirtual);
        Assert.Equal(original.Headline, loaded.Headline);

        // Replaying the loaded events yields the same results.
        RunRecording refolded = RunRecording.FromEvents(loaded.Events);
        Assert.Equal(original.Headline, refolded.Headline);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".csv")));
    }

    [Fact]
    public void SaveNeverOverwrites()
    {
        RunRecording r = RunRecording.FromEvents(Sample.Stream());
        string a = RecordingStore.Save(r, _dir);
        string b = RecordingStore.Save(r, _dir);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CsvHasOneRowPerRawIterationWithDiscardFlag()
    {
        var events = new List<BenchEvent>
        {
            new RunStartedEvent(Sample.Info(iterations: 3)) { T = 0 },
            new MeasurementEvent("git-clone", DriveRole.System, 1, 9) { T = 1 },
            new MeasurementEvent("git-clone", DriveRole.System, 2, 8) { T = 2 },
            new MeasurementEvent("git-clone", DriveRole.System, 3, 7) { T = 3 },
            new MeasurementEvent("git-clone", DriveRole.Dev, 1, 3) { T = 4 },
            new MeasurementEvent("git-clone", DriveRole.Dev, 2, 2) { T = 5 },
            new MeasurementEvent("git-clone", DriveRole.Dev, 3, 2) { T = 6 },
            new TestCompletedEvent("git-clone") { T = 7 },
            new RunCompletedEvent(RunOutcome.Completed) { T = 8 },
        };

        string[] lines = RecordingStore.ToCsv(RunRecording.FromEvents(events)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 6, lines.Length);
        Assert.Contains(",git-clone,", lines[1]);
        Assert.Contains(",System,C:,NTFS,false,false,1,9,true,7.5,", lines[1]);
        Assert.Contains(",Dev,D:,ReFS,true,true,3,2,false,2,", lines[6]);

        // Machine CPU contains a comma and must be quoted.
        Assert.Contains("\"Test CPU, 8 cores\"", lines[1]);

        // Tool provenance follows, so an emulated toolchain is visible in the data; git has no package cache.
        Assert.EndsWith(",git,2.56.0,x64,arm64,,", lines[1].TrimEnd('\r'));
    }

    [Fact]
    public void CsvRecordsWhereEachRowsPackagesLived()
    {
        RunInfo info = Sample.Info() with
        {
            Packages = PackageLocation.SystemDrive,
            Tests = new[] { new TestDescriptor { Id = "npm-ci", Group = "npm-ci", IsWorkload = true, Name = "npm ci", Unit = "s", Better = Better.Lower, PackageVariable = "npm_config_cache" } },
        };
        var events = new List<BenchEvent>
        {
            new RunStartedEvent(info) { T = 0 },
            new MeasurementEvent("npm-ci", DriveRole.System, 1, 8) { T = 1 },
            new MeasurementEvent("npm-ci", DriveRole.Dev, 1, 7) { T = 2 },
            new TestCompletedEvent("npm-ci") { T = 3 },
            new RunCompletedEvent(RunOutcome.Completed) { T = 4 },
        };

        string[] lines = RecordingStore.ToCsv(RunRecording.FromEvents(events)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",package_var,packages_drive", lines[0].TrimEnd('\r'));
        Assert.EndsWith(",npm_config_cache,C:", lines[1].TrimEnd('\r'));
        Assert.Contains(",Dev,D:,", lines[2]);
        Assert.EndsWith(",npm_config_cache,C:", lines[2].TrimEnd('\r'));
    }

    [Fact]
    public void ComparisonIsSavedWithTheRunAndPairsOnlyTheOtherLocation()
    {
        RunRecording onSystem = RunRecording.FromEvents(Sample.Stream());
        onSystem = onSystem with { Info = onSystem.Info with { Packages = PackageLocation.SystemDrive } };
        RunRecording onDev = RunRecording.FromEvents(Sample.Stream()) with { Comparison = ComparisonRun.From(onSystem) };

        RunRecording loaded = RecordingStore.Load(RecordingStore.Save(onDev, _dir));
        Assert.Equal(PackageLocation.DevDrive, loaded.Info.Packages);
        Assert.Equal(PackageLocation.SystemDrive, loaded.Comparison!.Packages);
        Assert.Equal(onSystem.Results.Count, loaded.Comparison.Results.Count);
        Assert.Equal(onSystem.Headline, loaded.Comparison.Headline);

        Assert.True(ComparisonRun.Pairs(onDev.Info, onSystem.Info));
        Assert.False(ComparisonRun.Pairs(onDev.Info, onDev.Info));
        Assert.False(ComparisonRun.Pairs(onDev.Info, onSystem.Info with { DevDrive = onSystem.Info.DevDrive with { Root = @"E:\" } }));
    }

    [Fact]
    public void RecordingsFromBeforePackageLocationsReadAsPackagesOnTheDevDrive()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(RunRecording.FromEvents(Sample.Stream()), RecordingStore.JsonOptions);
        json = json.Replace(",\"packages\":\"devDrive\"", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("\"packages\"", json);

        RunRecording old = System.Text.Json.JsonSerializer.Deserialize<RunRecording>(json, RecordingStore.JsonOptions)!;
        Assert.Equal(PackageLocation.DevDrive, old.Info.Packages);
        Assert.Null(old.Comparison);
    }

    [Fact]
    public void ListSkipsNonRecordingsAndFlagsBundled()
    {
        Directory.CreateDirectory(_dir);
        RecordingStore.Save(RunRecording.FromEvents(Sample.Stream()), _dir);
        File.WriteAllText(Path.Combine(_dir, "notes.json"), "{\"hello\":1}");

        IReadOnlyList<RecordingSummary> list = RecordingStore.List(userDirectory: null, bundledDirectory: _dir);
        RecordingSummary only = Assert.Single(list);
        Assert.True(only.IsBundled);
        Assert.Equal("BENCH", only.Machine);
    }
}

public class ReplaySchedulerTests
{
    [Fact]
    public void AdvanceReturnsDueEventsInOrderExactlyOnce()
    {
        var scheduler = new ReplayScheduler(Sample.Stream());
        Assert.Equal(14, scheduler.Duration);

        IReadOnlyList<BenchEvent> first = scheduler.Advance(1.0);
        Assert.Equal(4, first.Count);
        Assert.IsType<RunStartedEvent>(first[0]);

        Assert.Empty(scheduler.Advance(1.2));
        Assert.Equal(1.5, scheduler.NextEventTime);

        IReadOnlyList<BenchEvent> rest = scheduler.Advance(double.MaxValue);
        Assert.Equal(Sample.Stream().Count - 4, rest.Count);
        Assert.True(scheduler.IsFinished);
        Assert.Null(scheduler.NextEventTime);
    }

    [Fact]
    public void SameTimestampKeepsRecordedOrder()
    {
        var events = new BenchEvent[]
        {
            new TestStartedEvent("a") { T = 1 },
            new TestSkippedEvent("a", "x") { T = 1 },
            new TestStartedEvent("b") { T = 0.5 },
        };

        IReadOnlyList<BenchEvent> due = new ReplayScheduler(events).Advance(5);
        Assert.Equal("b", ((TestStartedEvent)due[0]).TestId);
        Assert.IsType<TestStartedEvent>(due[1]);
        Assert.IsType<TestSkippedEvent>(due[2]);
    }
}
