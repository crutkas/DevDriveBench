using System.Text.Json.Serialization;

namespace DevDriveBench.Engine.Model;

/// <summary>Which drive an event refers to.</summary>
public enum DriveRole
{
    None,
    System,
    Dev,
}

/// <summary>Direction of goodness for a metric.</summary>
public enum Better
{
    /// <summary>Wall-clock seconds: lower is better, advantage = system ÷ dev.</summary>
    Lower,

    /// <summary>Throughput / IOPS: higher is better, advantage = dev ÷ system.</summary>
    Higher,
}

/// <summary>What a test is doing right now.</summary>
public enum RunPhase
{
    /// <summary>One-time fixture seeding (may use the network); not measured.</summary>
    Prepare,

    /// <summary>Untimed per-iteration setup on the drive under test (cold cache + source copy).</summary>
    Setup,

    /// <summary>The timed section.</summary>
    Measure,

    /// <summary>Deleting fixtures.</summary>
    Cleanup,
}

/// <summary>How a run ended.</summary>
public enum RunOutcome
{
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// One timestamped fact in a run. The same stream drives the live view, is persisted verbatim in the
/// recording, and is re-applied by Replay at the original pace. <see cref="T"/> is seconds since run start.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RunStartedEvent), "runStarted")]
[JsonDerivedType(typeof(TestStartedEvent), "testStarted")]
[JsonDerivedType(typeof(PhaseStartedEvent), "phase")]
[JsonDerivedType(typeof(SampleEvent), "sample")]
[JsonDerivedType(typeof(MeasurementEvent), "measurement")]
[JsonDerivedType(typeof(TestSkippedEvent), "skipped")]
[JsonDerivedType(typeof(TestCompletedEvent), "testCompleted")]
[JsonDerivedType(typeof(MachineActivityEvent), "activity")]
[JsonDerivedType(typeof(RunCompletedEvent), "runCompleted")]
public abstract record BenchEvent
{
    /// <summary>Seconds since the run started (millisecond resolution).</summary>
    public double T { get; init; }
}

/// <summary>The run began; carries the full context (machine, drives, plan, tests).</summary>
public sealed record RunStartedEvent(RunInfo Info) : BenchEvent;

/// <summary>A test row became active.</summary>
public sealed record TestStartedEvent(string TestId) : BenchEvent;

/// <summary>A test entered a phase on a drive. <see cref="Iteration"/> is 1-based (0 when not per-iteration).</summary>
public sealed record PhaseStartedEvent(string TestId, RunPhase Phase, DriveRole Drive, int Iteration) : BenchEvent;

/// <summary>An instantaneous in-flight reading (disk passes): value in the test's unit, fraction = pass progress.</summary>
public sealed record SampleEvent(string TestId, DriveRole Drive, int Iteration, double Fraction, double Value) : BenchEvent;

/// <summary>One finished, raw per-iteration result in the test's unit.</summary>
public sealed record MeasurementEvent(string TestId, DriveRole Drive, int Iteration, double Value) : BenchEvent;

/// <summary>The test could not run (tool missing, no network, low disk, error).</summary>
public sealed record TestSkippedEvent(string TestId, string Reason) : BenchEvent;

/// <summary>The test finished all of its iterations.</summary>
public sealed record TestCompletedEvent(string TestId) : BenchEvent;

/// <summary>Periodic machine activity: disk MB/s on each drive's volume and total CPU %.</summary>
public sealed record MachineActivityEvent(double SystemMBps, double DevMBps, double CpuPercent) : BenchEvent;

/// <summary>The run ended.</summary>
public sealed record RunCompletedEvent(RunOutcome Outcome, string? Error = null) : BenchEvent;
