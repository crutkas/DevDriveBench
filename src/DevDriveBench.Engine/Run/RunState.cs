using DevDriveBench.Engine.Model;

namespace DevDriveBench.Engine.Run;

/// <summary>Lifecycle of one result row.</summary>
public enum TestStatus
{
    Pending,
    Running,
    Done,
    Skipped,

    /// <summary>Fixture prepared ahead and kept; the next Run measures straight away.</summary>
    Ready,
}

/// <summary>A contiguous span of a run spent in one phase (for the timeline ribbon).</summary>
public sealed record PhaseSpan(string TestId, RunPhase Phase, DriveRole Drive, double Start, double End);

/// <summary>Folded state of one row.</summary>
public sealed class TestState
{
    internal TestState(TestDescriptor descriptor) => Descriptor = descriptor;

    public TestDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public TestStatus Status { get; internal set; }

    public string? SkipReason { get; internal set; }

    /// <summary>Raw system-drive iteration values in completion order (iteration 1 first).</summary>
    public List<double> SystemRuns { get; } = new();

    /// <summary>Raw Dev Drive iteration values in completion order.</summary>
    public List<double> DevRuns { get; } = new();

    /// <summary>Live samples for the pass currently in flight on <see cref="ActiveDrive"/> (disk rows).</summary>
    public List<SampleEvent> LiveSamples { get; } = new();

    /// <summary>Most recent in-flight sample for each drive (disk rows), kept after the pass ends.</summary>
    public double? LastSystemSample { get; internal set; }

    public double? LastDevSample { get; internal set; }

    /// <summary>The drive this row is active on, or <see cref="DriveRole.None"/>.</summary>
    public DriveRole ActiveDrive { get; internal set; }

    public RunPhase? ActivePhase { get; internal set; }

    public int ActiveIteration { get; internal set; }

    /// <summary>Run time (s) the active phase began.</summary>
    public double ActivePhaseStart { get; internal set; }

    /// <summary>Seconds the fixture took to prepare in a Prepare session (0 when reused or not needed).</summary>
    public double? PrepareSeconds { get; internal set; }

    /// <summary>Reported system value (median after discarding the first when ≥ 2), or null before any run.</summary>
    public double? SystemValue => SystemRuns.Count > 0 ? BenchStats.Representative(SystemRuns) : null;

    /// <summary>Reported Dev Drive value, or null before any run.</summary>
    public double? DevValue => DevRuns.Count > 0 ? BenchStats.Representative(DevRuns) : null;

    /// <summary>Dev Drive advantage (×), or null until both sides have a value.</summary>
    public double? Advantage =>
        SystemValue is double s && DevValue is double d && BenchStats.Advantage(s, d, Descriptor.Better) is double a && a > 0
            ? a
            : null;

    /// <summary>Raw runs for a drive.</summary>
    public IReadOnlyList<double> RunsFor(DriveRole drive) => drive == DriveRole.Dev ? DevRuns : SystemRuns;
}

/// <summary>
/// Folds the event stream into the current picture of a run. Pure and deterministic, so the live view,
/// Replay, the CLI, and the tests all derive identical results from the same events.
/// </summary>
public sealed class RunState
{
    private readonly List<TestState> _tests = new();
    private readonly Dictionary<string, TestState> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PhaseSpan> _timeline = new();
    private readonly List<MachineActivityEvent> _activity = new();
    private PhaseStartedEvent? _openPhase;

    public RunInfo? Info { get; private set; }

    public IReadOnlyList<TestState> Tests => _tests;

    public IReadOnlyList<PhaseSpan> Timeline => _timeline;

    public IReadOnlyList<MachineActivityEvent> Activity => _activity;

    /// <summary>The phase currently open (for the "now" line), or null between phases.</summary>
    public PhaseStartedEvent? OpenPhase => _openPhase;

    public double LastEventTime { get; private set; }

    public RunOutcome? Outcome { get; private set; }

    public string? Error { get; private set; }

    public bool IsFinished => Outcome is not null;

    public TestState? Find(string id) => _byId.TryGetValue(id, out TestState? t) ? t : null;

    /// <summary>
    /// Headline: median Dev Drive advantage across real workloads that produced both values (the
    /// synthetic disk passes are reported separately and never inflate the headline).
    /// </summary>
    public double? Headline
    {
        get
        {
            double[] values = WorkloadAdvantages().ToArray();
            return values.Length == 0 ? null : BenchStats.Median(values);
        }
    }

    /// <summary>Number of workloads contributing to <see cref="Headline"/>.</summary>
    public int HeadlineCount => WorkloadAdvantages().Count();

    /// <summary>Applies one event. Events must arrive in non-decreasing <see cref="BenchEvent.T"/> order.</summary>
    public void Apply(BenchEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        LastEventTime = Math.Max(LastEventTime, e.T);

        switch (e)
        {
            case RunStartedEvent started:
                Reset(started.Info);
                break;

            case TestStartedEvent ts when Find(ts.TestId) is TestState t:
                if (t.Status == TestStatus.Pending)
                {
                    t.Status = TestStatus.Running;
                }

                break;

            case PhaseStartedEvent phase:
                ClosePhase(phase.T);
                _openPhase = phase;
                if (Find(phase.TestId) is TestState pt)
                {
                    pt.Status = pt.Status == TestStatus.Skipped ? TestStatus.Skipped : TestStatus.Running;
                    pt.ActiveDrive = phase.Drive;
                    pt.ActivePhase = phase.Phase;
                    pt.ActiveIteration = phase.Iteration;
                    pt.ActivePhaseStart = phase.T;
                    if (phase.Phase == RunPhase.Measure)
                    {
                        pt.LiveSamples.Clear();
                    }
                }

                break;

            case SampleEvent sample when Find(sample.TestId) is TestState st:
                st.LiveSamples.Add(sample);
                if (sample.Drive == DriveRole.Dev)
                {
                    st.LastDevSample = sample.Value;
                }
                else
                {
                    st.LastSystemSample = sample.Value;
                }

                break;

            case MeasurementEvent m when Find(m.TestId) is TestState mt:
                (m.Drive == DriveRole.Dev ? mt.DevRuns : mt.SystemRuns).Add(m.Value);
                if (_openPhase is { } open && string.Equals(open.TestId, m.TestId, StringComparison.OrdinalIgnoreCase) && open.Drive == m.Drive)
                {
                    ClosePhase(m.T);
                    _openPhase = null;
                }

                mt.ActivePhase = null;
                mt.ActiveDrive = DriveRole.None;
                break;

            case TestSkippedEvent skipped when Find(skipped.TestId) is TestState sk:
                CloseIfFor(skipped.TestId, skipped.T);
                sk.Status = TestStatus.Skipped;
                sk.SkipReason = skipped.Reason;
                sk.ActivePhase = null;
                sk.ActiveDrive = DriveRole.None;
                break;

            case TestCompletedEvent done when Find(done.TestId) is TestState dt:
                CloseIfFor(done.TestId, done.T);
                if (dt.Status != TestStatus.Skipped)
                {
                    dt.Status = TestStatus.Done;
                }

                dt.ActivePhase = null;
                dt.ActiveDrive = DriveRole.None;
                break;

            case TestPreparedEvent prepared when Find(prepared.TestId) is TestState pr:
                CloseIfFor(prepared.TestId, prepared.T);
                pr.Status = TestStatus.Ready;
                pr.PrepareSeconds = prepared.Seconds;
                pr.ActivePhase = null;
                pr.ActiveDrive = DriveRole.None;
                break;

            case MachineActivityEvent activity:
                _activity.Add(activity);
                break;

            case RunCompletedEvent completed:
                ClosePhase(completed.T);
                _openPhase = null;
                Outcome = completed.Outcome;
                Error = completed.Error;
                foreach (TestState t in _tests)
                {
                    t.ActivePhase = null;
                    t.ActiveDrive = DriveRole.None;
                }

                break;
        }
    }

    /// <summary>Per-test results for the recording's summary section.</summary>
    public IReadOnlyList<TestResult> Results() => _tests
        .Select(t => new TestResult
        {
            TestId = t.Id,
            Name = t.Descriptor.Name,
            Unit = t.Descriptor.Unit,
            Better = t.Descriptor.Better,
            Status = t.Status,
            SkipReason = t.SkipReason,
            SystemRuns = t.SystemRuns.ToArray(),
            DevRuns = t.DevRuns.ToArray(),
            SystemValue = t.SystemValue,
            DevValue = t.DevValue,
            Advantage = t.Advantage,
        })
        .ToArray();

    private IEnumerable<double> WorkloadAdvantages() => _tests
        .Where(t => t.Descriptor.IsWorkload && t.Status != TestStatus.Skipped && t.Advantage is not null)
        .Select(t => t.Advantage!.Value);

    private void Reset(RunInfo info)
    {
        Info = info;
        _tests.Clear();
        _byId.Clear();
        _timeline.Clear();
        _activity.Clear();
        _openPhase = null;
        Outcome = null;
        Error = null;
        foreach (TestDescriptor d in info.Tests)
        {
            var state = new TestState(d);
            _tests.Add(state);
            _byId[d.Id] = state;
        }
    }

    private void CloseIfFor(string testId, double t)
    {
        if (_openPhase is not null && string.Equals(_openPhase.TestId, testId, StringComparison.OrdinalIgnoreCase))
        {
            ClosePhase(t);
            _openPhase = null;
        }
    }

    private void ClosePhase(double end)
    {
        if (_openPhase is { } p && end >= p.T)
        {
            _timeline.Add(new PhaseSpan(p.TestId, p.Phase, p.Drive, p.T, end));
        }
    }
}

/// <summary>A row's final numbers, persisted alongside the raw events for easy analysis.</summary>
public sealed record TestResult
{
    public string TestId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    public Better Better { get; init; }

    public TestStatus Status { get; init; }

    public string? SkipReason { get; init; }

    public IReadOnlyList<double> SystemRuns { get; init; } = Array.Empty<double>();

    public IReadOnlyList<double> DevRuns { get; init; } = Array.Empty<double>();

    public double? SystemValue { get; init; }

    public double? DevValue { get; init; }

    public double? Advantage { get; init; }
}
