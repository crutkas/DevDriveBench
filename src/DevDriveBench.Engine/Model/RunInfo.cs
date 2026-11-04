using System.Text.Json.Serialization;

namespace DevDriveBench.Engine.Model;

/// <summary>A fixed volume and the facts that matter for interpreting its results.</summary>
public sealed record DriveDescriptor
{
    /// <summary>Volume root, e.g. <c>D:\</c>.</summary>
    public string Root { get; init; } = string.Empty;

    /// <summary>Volume label (may be empty).</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>File system, e.g. NTFS or ReFS.</summary>
    public string FileSystem { get; init; } = string.Empty;

    /// <summary>True when the volume carries the Dev Drive flag.</summary>
    public bool IsDevDrive { get; init; }

    /// <summary>True when the Dev Drive is trusted (AV performance mode eligible).</summary>
    public bool IsTrusted { get; init; }

    /// <summary>True for the Windows system volume.</summary>
    public bool IsSystem { get; init; }

    /// <summary>Total size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Free bytes when probed.</summary>
    public long FreeBytes { get; init; }

    /// <summary>Underlying disk friendly name, best-effort.</summary>
    public string? DiskModel { get; init; }

    /// <summary>Underlying bus type, e.g. NVMe, SATA, "File Backed Virtual" (VHD/VHDX).</summary>
    public string? BusType { get; init; }

    /// <summary>For virtual disks, the backing file (e.g. <c>C:\devdrive.vhdx</c>).</summary>
    public string? BackingFile { get; init; }

    /// <summary>Drive letter with colon, e.g. <c>D:</c>.</summary>
    [JsonIgnore]
    public string Letter => Root.Length >= 2 ? Root[..2].ToUpperInvariant() : Root;

    /// <summary>True when the volume is a VHD/VHDX file on another volume.</summary>
    [JsonIgnore]
    public bool IsVirtual => !string.IsNullOrEmpty(BackingFile) ||
        string.Equals(BusType, "File Backed Virtual", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The machine a run was recorded on.</summary>
public sealed record MachineInfo
{
    public string Name { get; init; } = string.Empty;

    public string Os { get; init; } = string.Empty;

    public string Cpu { get; init; } = string.Empty;

    public int LogicalCores { get; init; }

    public long MemoryBytes { get; init; }

    /// <summary>Native OS architecture: <c>arm64</c>, <c>x64</c>, or <c>x86</c> (true even if this app runs emulated).</summary>
    public string OsArchitecture { get; init; } = string.Empty;
}

/// <summary>One result row: what it measures, its unit and direction, and whether it can run here.</summary>
public sealed record TestDescriptor
{
    /// <summary>Stable workload id, e.g. <c>git-clone</c>; older recordings may include removed disk tests.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Selection group: the workload id (or <c>disk</c> in older recordings).</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>True for real developer workloads (these drive the headline).</summary>
    public bool IsWorkload { get; init; }

    /// <summary>Short display name, e.g. "git clone".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Short on-screen caption, e.g. "15,000 files".</summary>
    public string Caption { get; init; } = string.Empty;

    /// <summary>Full methodology detail (kept in the data, not on screen).</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Unit of every value: <c>s</c>, or <c>MB/s</c> / <c>IOPS</c> in older recordings.</summary>
    public string Unit { get; init; } = string.Empty;

    public Better Better { get; init; }

    /// <summary>Tool the workload needs (may be null in older recordings).</summary>
    public string? RequiredTool { get; init; }

    /// <summary>Detected tool version, when found.</summary>
    public string? ToolVersion { get; init; }

    /// <summary>The executable that really runs the workload (not a shim/proxy), when known.</summary>
    public string? ToolBinary { get; init; }

    /// <summary>PE machine type of <see cref="ToolBinary"/>: <c>arm64</c>, <c>x64</c>, or <c>x86</c>.</summary>
    public string? ToolArchitecture { get; init; }

    /// <summary>False when the test cannot run on this machine (e.g. tool not installed).</summary>
    public bool Available { get; init; } = true;

    /// <summary>Why <see cref="Available"/> is false.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>
    /// The environment variable that places this workload's package cache (<c>npm_config_cache</c>,
    /// <c>NUGET_PACKAGES</c>, <c>CARGO_HOME</c>), set only for the benchmark's own child processes;
    /// null when the test has no package cache.
    /// </summary>
    public string? PackageVariable { get; init; }
}

/// <summary>Everything needed to interpret (and replay) a run.</summary>
public sealed record RunInfo
{
    public string RunId { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public string AppVersion { get; init; } = string.Empty;

    public MachineInfo Machine { get; init; } = new();

    public DriveDescriptor SystemDrive { get; init; } = new();

    public DriveDescriptor DevDrive { get; init; } = new();

    /// <summary>Iterations per drive per test. The first is discarded when there are two or more.</summary>
    public int Iterations { get; init; } = 1;

    public WorkloadProfile Profile { get; init; } = WorkloadProfile.Thorough;

    /// <summary>Rows in execution order.</summary>
    public IReadOnlyList<TestDescriptor> Tests { get; init; } = Array.Empty<TestDescriptor>();

    /// <summary>Raw <c>fsutil devdrv query</c> output for the Dev Drive (AV filter / performance mode context).</summary>
    public string? DevDriveFilterInfo { get; init; }

    /// <summary>Whether this session measured (<see cref="SessionKind.Run"/>) or only prepared fixtures.</summary>
    public SessionKind Session { get; init; } = SessionKind.Run;

    /// <summary>
    /// True when at least one workload was prepared before this run started, so its one-time Prepare
    /// isn't in this recording's timeline (see <c>RunRecording.Preparation</c>).
    /// </summary>
    public bool PreparedAhead { get; init; }

    /// <summary>
    /// Where the Dev Drive run's package caches lived (<c>npm_config_cache</c>, <c>NUGET_PACKAGES</c>,
    /// <c>CARGO_HOME</c>). The system-drive run always uses the system drive. Recordings made before this
    /// existed kept caches on the drive under test, which is <see cref="PackageLocation.DevDrive"/>.
    /// </summary>
    public PackageLocation Packages { get; init; } = PackageLocation.DevDrive;
}

/// <summary>Which drive holds the package caches for the Dev Drive run.</summary>
public enum PackageLocation
{
    /// <summary>Code and packages both on the Dev Drive.</summary>
    DevDrive,

    /// <summary>Code on the Dev Drive, packages still on the system drive.</summary>
    SystemDrive,
}
