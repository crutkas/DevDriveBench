using DevDriveBench.Engine.Model;

namespace DevDriveBench.Engine.Run;

/// <summary>One timed section of a run.</summary>
public sealed record MeasuredSpan(string TestId, DriveRole Drive, int Iteration, double Start, double End)
{
    public string Label => $"{TestId} {Drive} #{Iteration}";
}

/// <summary>Untimed wall-clock between two measured sections (or the run's start/end).</summary>
public sealed record TimingGap(string After, string Before, double Seconds);

/// <summary>
/// How much of a run the audience spends watching measurements versus waiting: total duration, time
/// inside timed sections, and every gap between them. Derived purely from the event stream.
/// </summary>
public sealed record StageTiming(double Total, double Measured, IReadOnlyList<MeasuredSpan> Spans, IReadOnlyList<TimingGap> Gaps)
{
    /// <summary>Wall-clock not spent inside a timed section.</summary>
    public double DeadTime => Math.Max(0, Total - Measured);

    public static StageTiming From(IReadOnlyList<BenchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var open = new Dictionary<(string, DriveRole, int), double>();
        var spans = new List<MeasuredSpan>();
        double total = 0;
        foreach (BenchEvent e in events)
        {
            total = Math.Max(total, e.T);
            switch (e)
            {
                case PhaseStartedEvent { Phase: RunPhase.Measure } p:
                    open[(p.TestId, p.Drive, p.Iteration)] = p.T;
                    break;
                case MeasurementEvent m when open.Remove((m.TestId, m.Drive, m.Iteration), out double start):
                    spans.Add(new MeasuredSpan(m.TestId, m.Drive, m.Iteration, start, m.T));
                    break;
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var gaps = new List<TimingGap>();
        string previous = "start";
        double cursor = 0;
        foreach (MeasuredSpan span in spans)
        {
            gaps.Add(new TimingGap(previous, span.Label, Math.Max(0, span.Start - cursor)));
            previous = span.Label;
            cursor = Math.Max(cursor, span.End);
        }

        gaps.Add(new TimingGap(previous, "end", Math.Max(0, total - cursor)));
        double measured = spans.Sum(s => s.End - s.Start);
        return new StageTiming(total, measured, spans, gaps);
    }
}
