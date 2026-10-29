// Ported from crutkas/devdrive (branch crutkas-drive-space-analyzer), MIT License.
// Extended with IDiskBenchmarkObserver so a live view can stream instantaneous throughput.
using DevDriveBench.Engine.Model;

namespace DevDriveBench.Engine.Abstractions;

/// <summary>
/// Tunables for a single benchmark run. Defaults keep the run modest (≈128 MiB, a few seconds per
/// drive) so the speed test is safe to run interactively and never fills the disk.
/// </summary>
public sealed record DiskBenchmarkOptions
{
    /// <summary>Total bytes written/read for the sequential passes (default 128 MiB). Rounded down to a 4 KiB multiple.</summary>
    public long FileSizeBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Block size for the sequential passes (default 1 MiB).</summary>
    public int SequentialBlockBytes { get; init; } = 1 * 1024 * 1024;

    /// <summary>Block size for the random passes (default 4 KiB).</summary>
    public int RandomBlockBytes { get; init; } = 4096;

    /// <summary>Number of random 4 KiB operations per pass (default 2000).</summary>
    public int RandomOperations { get; init; } = 2000;

    /// <summary>Free space that must remain after allocating the test file (default 256 MiB).</summary>
    public long FreeSpaceMarginBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Approximate number of live samples reported per pass (for the live trace).</summary>
    public int SamplesPerPass { get; init; } = 24;

    /// <summary>The default option set.</summary>
    public static DiskBenchmarkOptions Default => new();
}

/// <summary>The four synthetic disk passes, in the order <see cref="IDiskBenchmark"/> runs them.</summary>
public enum DiskMetric
{
    SequentialWrite,
    SequentialRead,
    RandomRead4K,
    RandomWrite4K,
}

/// <summary>
/// Receives live progress from a disk benchmark run. Called synchronously on the benchmark thread,
/// so implementations must be cheap (e.g. enqueue and return).
/// </summary>
public interface IDiskBenchmarkObserver
{
    /// <summary>A pass is about to start its timed section.</summary>
    void MetricStarted(DiskMetric metric);

    /// <summary>
    /// An instantaneous sample over the most recent window: <paramref name="value"/> is MB/s for the
    /// sequential passes and IOPS for the random passes; <paramref name="fraction"/> is pass progress 0..1.
    /// </summary>
    void Sample(DiskMetric metric, double fraction, double value);

    /// <summary>A pass finished; <paramref name="value"/> is its whole-pass result.</summary>
    void MetricCompleted(DiskMetric metric, double value);
}

/// <summary>
/// Lowest-level disk-benchmark seam: runs a real, bounded synthetic benchmark against a single
/// drive and returns its throughput/IOPS.
/// </summary>
/// <remarks>
/// The real implementation creates its temporary file under a benchmark folder on the target drive
/// and ALWAYS deletes it (and the folder, if it created it) in a <c>finally</c> block.
/// </remarks>
public interface IDiskBenchmark
{
    /// <summary>
    /// Runs the benchmark against <paramref name="driveRoot"/> (e.g. <c>D:\</c>). Throws
    /// <see cref="InvalidOperationException"/> when there is insufficient free space, and surfaces
    /// platform failures as exceptions so the caller can degrade gracefully.
    /// </summary>
    DiskBenchmarkResult Run(
        string driveRoot,
        DiskBenchmarkOptions options,
        IDiskBenchmarkObserver? observer = null,
        CancellationToken cancellationToken = default);
}
