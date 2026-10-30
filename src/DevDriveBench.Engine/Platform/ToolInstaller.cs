using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DevDriveBench.Engine.Run;

namespace DevDriveBench.Engine.Platform;

/// <summary>
/// A winget package; <see cref="Override"/> is an optional <c>--override</c> argument string,
/// <see cref="PinArchitecture"/> asks winget for the installer matching the OS architecture, and
/// <see cref="PostInstall"/> is an optional <c>(tool, arguments)</c> step run once PATH is refreshed.
/// </summary>
public sealed record WingetPackage(string Id, string Name, string? Override = null, bool PinArchitecture = true, (string Tool, string Arguments)? PostInstall = null);

/// <summary>What each workload needs from winget to run.</summary>
public static class InstallCatalog
{
    /// <summary>
    /// Packages for a workload group, in install order. cargo needs the MSVC linker, so the C++ build
    /// tools (with the native ARM64 toolset on ARM64 machines) are installed before rustup.
    /// </summary>
    public static IReadOnlyList<WingetPackage> For(string group) => group switch
    {
        TestCatalog.GitClone => new[] { new WingetPackage("Git.Git", "Git") },
        TestCatalog.NpmCi => new[] { new WingetPackage("OpenJS.NodeJS.LTS", "Node.js LTS") },
        TestCatalog.DotnetBuild => new[] { new WingetPackage("Microsoft.DotNet.SDK.10", ".NET 10 SDK") },
        TestCatalog.CargoBuild => new[] { MsvcBuildTools(RuntimeInformation.OSArchitecture), Rustup },
        _ => Array.Empty<WingetPackage>(),
    };

    /// <summary>
    /// rustup from winget installs only the toolchain manager (no default toolchain, so <c>cargo</c>
    /// can't run); setting the default downloads the stable toolchain for this host.
    /// </summary>
    public static WingetPackage Rustup { get; } = new("Rustlang.Rustup", "Rust (rustup)", PostInstall: ("rustup", "default stable"));

    /// <summary>
    /// VS 2022 Build Tools with the C++ workload (and the ARM64 toolset on ARM64). Its bootstrapper is
    /// architecture-neutral, so the toolset is chosen by the <c>--add</c> override, not <c>--architecture</c>.
    /// </summary>
    public static WingetPackage MsvcBuildTools(Architecture os)
    {
        string arm64 = os == Architecture.Arm64 ? " --add Microsoft.VisualStudio.Component.VC.Tools.ARM64" : string.Empty;
        return new WingetPackage(
            "Microsoft.VisualStudio.2022.BuildTools",
            "MSVC Build Tools",
            $"--wait --passive --norestart --nocache --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended{arm64}",
            PinArchitecture: false);
    }

    /// <summary>winget's name for an architecture (<c>arm64</c>, <c>x64</c>, <c>x86</c>), or null.</summary>
    public static string? WingetArchitecture(Architecture os) => os switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        _ => null,
    };

    /// <summary>The <c>winget install</c> arguments for this machine's OS architecture.</summary>
    public static string Arguments(WingetPackage package) => Arguments(package, RuntimeInformation.OSArchitecture);

    /// <summary>
    /// The <c>winget install</c> arguments for one package (non-interactive; installers may still ask
    /// for elevation). Pinned packages request the native installer so an ARM64 machine never gets an
    /// emulated x64 toolchain by default.
    /// </summary>
    public static string Arguments(WingetPackage package, Architecture os)
    {
        var args = new StringBuilder($"install --id {package.Id} --exact --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity");
        if (package.PinArchitecture && WingetArchitecture(os) is string arch)
        {
            args.Append(" --architecture ").Append(arch);
        }

        if (package.Override is string o)
        {
            args.Append(" --override \"").Append(o).Append('"');
        }

        return args.ToString();
    }
}

/// <summary>Result of installing a workload's packages.</summary>
public sealed record InstallResult(bool Succeeded, string Message);

/// <summary>
/// Installs packages with winget, streaming short status lines, then refreshes this process's PATH
/// so newly installed tools are found (by detection and by every workload process) without a restart.
/// </summary>
public sealed class WingetInstaller
{
    /// <summary>True when winget is on PATH.</summary>
    public static bool IsAvailable => ToolProbe.Resolve("winget") is not null;

    public Task<InstallResult> InstallAsync(IReadOnlyList<WingetPackage> packages, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Install(packages, progress, cancellationToken), cancellationToken);

    private static InstallResult Install(IReadOnlyList<WingetPackage> packages, IProgress<string>? progress, CancellationToken ct)
    {
        string? winget = ToolProbe.Resolve("winget");
        if (winget is null)
        {
            return new InstallResult(false, "winget is not available");
        }

        foreach (WingetPackage package in packages)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{package.Name} · starting");
            (int exit, string last, string all) = Run(winget, InstallCatalog.Arguments(package), line => progress?.Report($"{package.Name} · {line}"), ct);
            if (!IsSuccess(exit, all))
            {
                EnvironmentRefresher.RefreshPath();
                return new InstallResult(false, $"{package.Name}: {(string.IsNullOrEmpty(last) ? $"winget exit 0x{exit:X8}" : last)}");
            }

            if (package.PostInstall is (string tool, string arguments))
            {
                EnvironmentRefresher.RefreshPath();
                string? exe = ToolProbe.Resolve(tool);
                if (exe is null)
                {
                    return new InstallResult(false, $"{package.Name}: {tool} not found after install");
                }

                progress?.Report($"{package.Name} · {tool} {arguments}");
                (int postExit, string postLast, _) = Run(exe, arguments, line => progress?.Report($"{package.Name} · {line}"), ct);
                if (postExit != 0)
                {
                    return new InstallResult(false, $"{tool} {arguments}: {(string.IsNullOrEmpty(postLast) ? $"exit {postExit}" : postLast)}");
                }
            }
        }

        EnvironmentRefresher.RefreshPath();
        return new InstallResult(true, "installed");
    }

    /// <summary>winget exit 0, or a "nothing to do" outcome (already installed / no newer version).</summary>
    public static bool IsSuccess(int exitCode, string output) =>
        exitCode == 0 ||
        output.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("No available upgrade found", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("No newer package versions", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reduces winget console output (progress bars, spinners) to a short status line, or null.</summary>
    public static string? StatusLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Progress redraws use carriage returns; keep the last segment that says something.
        string? segment = raw.Split('\r')
            .Select(s => new string(s.Where(c => !char.IsControl(c) && c is not ('█' or '▒' or '░' or '▓')).ToArray()).Trim())
            .LastOrDefault(s => s.Count(char.IsLetter) >= 3);
        if (string.IsNullOrEmpty(segment))
        {
            return null;
        }

        return segment.Length > 90 ? string.Concat(segment.AsSpan(0, 90), "…") : segment;
    }

    private static (int Exit, string Last, string All) Run(string fileName, string arguments, Action<string> onStatus, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            },
        };

        var all = new StringBuilder();
        string last = string.Empty;
        object gate = new();
        void OnData(string? data)
        {
            if (StatusLine(data) is string line)
            {
                lock (gate)
                {
                    all.AppendLine(line);
                    last = line;
                }

                onStatus(line);
            }
        }

        process.OutputDataReceived += (_, e) => OnData(e.Data);
        process.ErrorDataReceived += (_, e) => OnData(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        while (!process.WaitForExit(250))
        {
            if (ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                ct.ThrowIfCancellationRequested();
            }
        }

        process.WaitForExit();
        lock (gate)
        {
            return (process.ExitCode, last, all.ToString());
        }
    }
}

/// <summary>Re-reads PATH from the registry (machine + user) so tools installed after launch resolve.</summary>
public static class EnvironmentRefresher
{
    /// <summary>Updates this process's PATH; child processes (git, npm, cargo, dotnet) inherit it.</summary>
    public static void RefreshPath()
    {
        string merged = MergePath(
            Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH"));
        Environment.SetEnvironmentVariable("PATH", merged);
    }

    /// <summary>Machine entries, then user entries (Windows' order), then any process-only entries; expanded and de-duplicated.</summary>
    public static string MergePath(string? machine, string? user, string? current)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string? source in new[] { machine, user, current })
        {
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            foreach (string raw in source.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string entry = Environment.ExpandEnvironmentVariables(raw).TrimEnd('\\');
                if (entry.Length > 0 && seen.Add(entry))
                {
                    result.Add(entry);
                }
            }
        }

        return string.Join(';', result);
    }
}
