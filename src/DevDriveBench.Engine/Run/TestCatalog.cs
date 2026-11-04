using DevDriveBench.Engine.Model;

namespace DevDriveBench.Engine.Run;

/// <summary>Stable ids, display names, and units for every result row.</summary>
public static class TestCatalog
{
    public const string GitClone = "git-clone";
    public const string NpmCi = "npm-ci";
    public const string DotnetBuild = "dotnet-build";
    public const string CargoBuild = "cargo-build";

    /// <summary>
    /// Selectable developer workloads in execution order: the offline git clone, then network-seeded builds.
    /// </summary>
    public static IReadOnlyList<string> GroupOrder { get; } = new[] { GitClone, NpmCi, DotnetBuild, CargoBuild };

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

    /// <summary>
    /// The variable that places a workload's package cache (benchmark child processes only), or null
    /// for tests without one.
    /// </summary>
    public static string? PackageVariable(string id) => id switch
    {
        NpmCi => "npm_config_cache",
        DotnetBuild => "NUGET_PACKAGES",
        CargoBuild => "CARGO_HOME",
        _ => null,
    };

    /// <summary>Short on-screen name for a package location, e.g. "D:" or "C:".</summary>
    public static string PackagesLetter(PackageLocation packages, RunInfo info) =>
        packages == PackageLocation.SystemDrive ? info.SystemDrive.Letter : info.DevDrive.Letter;

    /// <summary>The package mode in the app's own words: "optimal drive" or "C drive".</summary>
    public static string PackagesMode(PackageLocation packages, RunInfo info) =>
        packages == PackageLocation.SystemDrive ? info.SystemDrive.Letter.TrimEnd(':') + " drive" : "optimal drive";

    /// <summary>Display name for a selectable group.</summary>
    public static string GroupName(string group) => group switch
    {
        GitClone => "git clone",
        NpmCi => "npm ci",
        DotnetBuild => "dotnet build",
        CargoBuild => "cargo build",
        _ => group,
    };

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

}
