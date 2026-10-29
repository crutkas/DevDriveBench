// Ported from crutkas/devdrive (branch crutkas-drive-space-analyzer), MIT License.
namespace DevDriveBench.Engine.Platform;

/// <summary>
/// Shared folder names for the benchmarks. Bounded temp fixtures live under these names so cleanup can
/// target them deterministically.
/// </summary>
internal static class WorkloadPaths
{
    /// <summary>Per-drive working area for the real workloads, e.g. <c>D:\DevDriveBench.Workload</c>.</summary>
    public const string BenchFolderName = "DevDriveBench.Workload";

    /// <summary>Neutral shared-fixture area, created under <c>%TEMP%</c>.</summary>
    public const string SeedFolderName = "DevDriveBench.Seed";

    /// <summary>Per-drive folder holding the synthetic disk test's temporary file.</summary>
    public const string DiskFolderName = "DevDriveBench.Disk";
}
