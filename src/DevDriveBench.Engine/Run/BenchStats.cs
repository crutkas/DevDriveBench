namespace DevDriveBench.Engine.Run;

/// <summary>
/// Methodology math. With two or more iterations the first (warm-up: process start, JIT, OS caches) is
/// discarded and the median of the rest is reported; with a single iteration that run is reported as-is.
/// </summary>
public static class BenchStats
{
    /// <summary>Median; mean of the middle two for an even count; 0 for none. Does not mutate the input.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values is null || values.Count == 0)
        {
            return 0d;
        }

        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2d;
    }

    /// <summary>True when iteration <paramref name="index"/> (0-based) of <paramref name="count"/> is the discarded warm-up.</summary>
    public static bool IsDiscarded(int index, int count) => count >= 2 && index == 0;

    /// <summary>The reported value: discard the first run when there are at least two, then take the median.</summary>
    public static double Representative(IReadOnlyList<double> runs)
    {
        if (runs is null || runs.Count == 0)
        {
            return 0d;
        }

        return runs.Count == 1 ? runs[0] : Median(runs.Skip(1).ToArray());
    }

    /// <summary>
    /// Dev Drive advantage (×): system ÷ dev for lower-is-better seconds, dev ÷ system for
    /// higher-is-better throughput. Above 1 means the Dev Drive won. 0 when either side is missing.
    /// </summary>
    public static double Advantage(double system, double dev, Model.Better better)
    {
        if (system <= 0d || dev <= 0d)
        {
            return 0d;
        }

        return better == Model.Better.Lower ? system / dev : dev / system;
    }
}
