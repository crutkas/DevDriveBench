using System.Text.Json;
using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Platform;
using DevDriveBench.Engine.Run;

namespace DevDriveBench.Engine.Tests;

public class WorkloadFailureTests
{
    [Fact]
    public void BuildErrorWinsOverSuccessfulProjectOutputAndStderrWarning()
    {
        const string error = "Catalog.API.csproj : error MSB3073: OpenAPI generation failed.";
        var result = new ProcessRunResult(1, $"EventBus -> C:\\error\\EventBus.dll\n{error}\nBuild FAILED.", "A warning");

        string reason = Assert.Throws<WorkloadUnavailableException>(() => FailureWorkload.Check(result)).Message;

        Assert.Equal($"dotnet build failed (exit 1): {error}", reason.Split('\n')[0]);
        Assert.Contains(result.StandardOutput, reason);
        Assert.Contains(result.StandardError, reason);
    }

    [Fact]
    public void FullMultilineErrorsSurviveRecordingRoundTrip()
    {
        string output = $"EventBus -> EventBus.dll\n{new string('x', 400)} : error CS0001: first failure\n  additional context\nOther.cs : error CS0002: second failure\nBuild FAILED.";
        string reason = Assert.Throws<WorkloadUnavailableException>(() =>
            FailureWorkload.Check(new ProcessRunResult(1, output, ""))).Message;
        var events = new BenchEvent[]
        {
            new RunStartedEvent(Sample.Info()),
            new TestSkippedEvent("git-clone", reason),
        };
        RunRecording recording = RunRecording.FromEvents(events);
        RunRecording loaded = JsonSerializer.Deserialize<RunRecording>(JsonSerializer.Serialize(recording))!;

        Assert.Contains(output, reason);
        Assert.Equal(reason, loaded.Results.Single(result => result.TestId == "git-clone").SkipReason);
        Assert.Contains("error CS0001: first failure", reason.Split('\n')[0]);
        Assert.True(reason.Split('\n')[0].Length < 300);
    }

    [Fact]
    public void TimeoutPreservesPartialOutput()
    {
        var result = new ProcessRunResult(-1, "partial stdout", "partial stderr") { TimedOut = true };
        string reason = Assert.Throws<WorkloadUnavailableException>(() => FailureWorkload.Check(result)).Message;
        Assert.StartsWith("dotnet build timed out:", reason);
        Assert.Contains("partial stdout", reason);
        Assert.Contains("partial stderr", reason);
    }

    [Fact]
    public void EmptyOutputStillReportsExitCode()
    {
        string reason = Assert.Throws<WorkloadUnavailableException>(() =>
            FailureWorkload.Check(new ProcessRunResult(1, "", ""))).Message;
        Assert.Equal("dotnet build failed (exit 1)", reason);
    }

    [Fact]
    public void SuccessfulOutputIsNotAnError()
    {
        FailureWorkload.Check(new ProcessRunResult(0, "0 Error(s)", ""));
    }

    [Fact]
    public void StderrOnlyLaunchFailureIsPreserved()
    {
        const string stderr = "The executable could not be started.\nAccess is denied.";
        string reason = Assert.Throws<WorkloadUnavailableException>(() =>
            FailureWorkload.Check(new ProcessRunResult(-1, "", stderr))).Message;
        Assert.StartsWith("dotnet build failed (exit -1): The executable could not be started.", reason);
        Assert.Contains(stderr, reason);
    }

    private sealed class FailureWorkload : WorkloadBenchmarkBase
    {
        private FailureWorkload() : base(new WorkloadProcessRunner()) { }

        public static void Check(ProcessRunResult result) => EnsureSuccess(result, "dotnet build");

        public override string Name => "test";
        public override string Detail => "test";
        public override string RequiredTool => "none";
        protected override string Slug => "test";
        protected override void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken) => throw new NotSupportedException();
        protected override double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
