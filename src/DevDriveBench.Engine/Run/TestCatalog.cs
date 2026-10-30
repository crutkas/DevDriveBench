using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Model;

namespace DevDriveBench.Engine.Run;

/// <summary>Stable ids, display names, and units for every result row.</summary>
public static class TestCatalog
{
    public const string DiskGroup = "disk";
    public const string GitClone = "git-clone";
    public const string NpmCi = "npm-ci";
    public const string DotnetBuild = "dotnet-build";
    public const string CargoBuild = "cargo-build";

    public const string DiskSeqWrite = "disk-seq-write";
    public const string DiskSeqRead = "disk-seq-read";
    public const string DiskRandRead = "disk-rand-read";
    public const string DiskRandWrite = "disk-rand-write";

    /// <summary>
    /// Selectable groups in execution order: the seconds-long synthetic disk pass first (while the machine
    /// is quiet, before any workload leaves background AV/indexer churn behind), then the offline git
    /// clone, then the network-seeded builds.
    /// </summary>
    public static IReadOnlyList<string> GroupOrder { get; } = new[] { DiskGroup, GitClone, NpmCi, DotnetBuild, CargoBuild };

    /// <summary>Workload id for a required tool.</summary>
    public static string WorkloadId(string requiredTool) => requiredTool.ToLowerInvariant() switch
    {
        "git" => GitClone,
        "npm" => NpmCi,
        "dotnet" => DotnetBuild,
        "cargo" => CargoBuild,
        _ => requiredTool.ToLowerInvariant() + "-workload",
    };

    /// <summary>Short on-screen caption for a workload.</summary>
    public static string WorkloadCaption(string id, WorkloadProfile profile) => id switch
    {
        GitClone => profile == WorkloadProfile.Thorough ? "15,000 files" : "1,200 files",
        NpmCi => "vscode-eslint · offline",
        DotnetBuild => "eShop · .NET 10",
        CargoBuild => "microsoft/edit · offline",
        _ => string.Empty,
    };

    /// <summary>Display name for a selectable group.</summary>
    public static string GroupName(string group) => group switch
    {
        DiskGroup => "Disk I/O",
        GitClone => "git clone",
        NpmCi => "npm ci",
        DotnetBuild => "dotnet build",
        CargoBuild => "cargo build",
        _ => group,
    };

    /// <summary>Row id for a disk pass.</summary>
    public static string DiskId(DiskMetric metric) => metric switch
    {
        DiskMetric.SequentialWrite => DiskSeqWrite,
        DiskMetric.SequentialRead => DiskSeqRead,
        DiskMetric.RandomRead4K => DiskRandRead,
        _ => DiskRandWrite,
    };

    /// <summary>The four disk rows, in the order the benchmark runs them.</summary>
    public static IReadOnlyList<TestDescriptor> DiskTests(DiskBenchmarkOptions options)
    {
        long mib = options.FileSizeBytes / (1024 * 1024);
        string ops = options.RandomOperations.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        return new[]
        {
            Disk(DiskSeqWrite, "Sequential write", "MB/s", $"{mib} MiB · 1 MiB blocks", $"{mib} MiB file, 1 MiB blocks, unbuffered + write-through"),
            Disk(DiskSeqRead, "Sequential read", "MB/s", $"{mib} MiB · 1 MiB blocks", $"{mib} MiB file, 1 MiB blocks, unbuffered"),
            Disk(DiskRandRead, "Random read 4K", "IOPS", $"{ops} ops · QD1", $"{ops} random 4 KiB reads, queue depth 1, unbuffered"),
            Disk(DiskRandWrite, "Random write 4K", "IOPS", $"{ops} ops · QD1", $"{ops} random 4 KiB writes, queue depth 1, unbuffered + write-through"),
        };
    }

    /// <summary>
    /// "x64 node, emulated" when the tool's real binary doesn't match the OS architecture (e.g. an x64
    /// toolchain on an ARM64 machine, which runs emulated and skews results); null when native or unknown.
    /// </summary>
    public static string? EmulationNote(TestDescriptor test, MachineInfo machine)
    {
        if (string.IsNullOrEmpty(test.ToolArchitecture) || string.IsNullOrEmpty(machine.OsArchitecture) ||
            string.Equals(test.ToolArchitecture, machine.OsArchitecture, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string binary = string.IsNullOrEmpty(test.ToolBinary)
            ? test.RequiredTool ?? "tool"
            : Path.GetFileNameWithoutExtension(test.ToolBinary.Replace('/', '\\').Split('\\')[^1]);
        return $"{test.ToolArchitecture} {binary}, emulated";
    }

    private static TestDescriptor Disk(string id, string name, string unit, string caption, string detail) => new()
    {
        Id = id,
        Group = DiskGroup,
        IsWorkload = false,
        Name = name,
        Caption = caption,
        Detail = detail,
        Unit = unit,
        Better = Better.Higher,
    };
}
