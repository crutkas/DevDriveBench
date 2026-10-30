using System.Runtime.InteropServices;
using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Model;
using Microsoft.Win32;

namespace DevDriveBench.Engine.Platform;

/// <summary>
/// An installed developer tool, resolved on PATH. <see cref="Binary"/> is the executable that actually
/// does the work (not a shim or proxy) and <see cref="Architecture"/> its PE machine type
/// (<c>arm64</c>, <c>x64</c>, <c>x86</c>), so an emulated tool on an ARM64 machine is visible.
/// </summary>
public sealed record ToolInfo(string Name, bool Found, string? Version, string? Path, string? Binary = null, string? Architecture = null);

/// <summary>Detects whether a workload's tool is installed (read-only: PATH lookup + version probe).</summary>
public interface IToolProbe
{
    ToolInfo Detect(string tool);
}

/// <summary>Real <see cref="IToolProbe"/>: PATH + PATHEXT resolution, a <c>--version</c> probe, and the real binary's architecture.</summary>
public sealed class ToolProbe : IToolProbe
{
    private readonly IWorkloadProcessRunner _runner;

    public ToolProbe(IWorkloadProcessRunner? runner = null) => _runner = runner ?? new WorkloadProcessRunner();

    public ToolInfo Detect(string tool)
    {
        string? path = Resolve(tool);
        if (path is null)
        {
            return new ToolInfo(tool, false, null, null);
        }

        // .cmd/.bat shims (npm) must go through cmd.exe; real executables launch directly.
        bool isScript = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        ProcessRunResult result = isScript
            ? _runner.Run(new WorkloadProcessRequest("cmd.exe", $"/c \"\"{path}\" --version\"", TimeoutMs: 15_000))
            : _runner.Run(new WorkloadProcessRequest(path, "--version", TimeoutMs: 15_000));

        string? version = result.ExitCode == 0 ? ParseVersion(result.StandardOutput + "\n" + result.StandardError) : null;
        if (result.ExitCode != 0)
        {
            // On PATH but can't run (e.g. a rustup proxy with no toolchain): not usable for a workload.
            return new ToolInfo(tool, false, null, path);
        }

        string? binary = RealBinary(tool, path);
        return new ToolInfo(tool, true, version, path, binary, binary is null ? null : PeArchitecture(binary));
    }

    /// <summary>
    /// The executable that really runs for a tool: git's own <c>git.exe</c> in its exec-path (the
    /// <c>cmd\git.exe</c> on PATH is a launcher), <c>node.exe</c> for the <c>npm.cmd</c> shim, the
    /// active toolchain's <c>cargo.exe</c> (<c>.cargo\bin\cargo.exe</c> is a rustup proxy), else the
    /// resolved path itself. Null when it can't be determined.
    /// </summary>
    private string? RealBinary(string tool, string resolved)
    {
        try
        {
            switch (tool.ToLowerInvariant())
            {
                case "git":
                    // exec-path is <prefix>\libexec\git-core; the real git.exe is <prefix>\bin\git.exe
                    // (older layouts also keep one in git-core).
                    string? execPath = FirstLine(_runner.Run(new WorkloadProcessRequest(resolved, "--exec-path", TimeoutMs: 15_000)))?.Replace('/', '\\');
                    if (execPath is not null)
                    {
                        string? prefix = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(execPath.TrimEnd('\\')));
                        foreach (string candidate in new[] { System.IO.Path.Combine(execPath, "git.exe"), prefix is null ? string.Empty : System.IO.Path.Combine(prefix, "bin", "git.exe") })
                        {
                            if (candidate.Length > 0 && File.Exists(candidate))
                            {
                                return candidate;
                            }
                        }
                    }

                    return resolved;

                case "npm":
                    string? node = Resolve("node");
                    return node is not null && node.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? node : null;

                case "cargo":
                    string? rustup = Resolve("rustup");
                    string? cargo = rustup is null ? null : FirstLine(_runner.Run(new WorkloadProcessRequest(rustup, "which cargo", TimeoutMs: 15_000)));
                    return cargo is not null && File.Exists(cargo) ? cargo : resolved;

                default:
                    return resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? resolved : null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstLine(ProcessRunResult result) =>
        result.ExitCode != 0
            ? null
            : result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    /// <summary>The PE machine type of an executable as <c>arm64</c>/<c>x64</c>/<c>x86</c>, or null.</summary>
    public static string? PeArchitecture(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            {
                return null; // no "MZ"
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > stream.Length)
            {
                return null;
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return null; // no "PE\0\0"
            }

            return MachineName(reader.ReadUInt16());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Names an IMAGE_FILE_MACHINE value.</summary>
    public static string? MachineName(ushort machine) => machine switch
    {
        0xAA64 => "arm64",
        0x8664 => "x64",
        0x014C => "x86",
        _ => null,
    };

    /// <summary>First <c>MAJOR.MINOR[.PATCH]</c> token in the text, or null.</summary>
    public static string? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+\.\d+(?:\.\d+)?");
        return match.Success ? match.Value : null;
    }

    /// <summary>Resolves an executable name against PATH and PATHEXT.</summary>
    public static string? Resolve(string tool)
    {
        string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string dir in dirs)
        {
            foreach (string ext in extensions)
            {
                try
                {
                    string candidate = System.IO.Path.Combine(dir.Trim('"'), tool + ext.ToLowerInvariant());
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Malformed PATH entry.
                }
            }
        }

        return null;
    }
}

/// <summary>Describes the current machine for the recording.</summary>
public static class MachineProbe
{
    public static MachineInfo Capture() => new()
    {
        Name = Environment.MachineName,
        Os = $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})",
        Cpu = CpuName(),
        LogicalCores = Environment.ProcessorCount,
        MemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        OsArchitecture = InstallCatalog.WingetArchitecture(RuntimeInformation.OSArchitecture) ?? RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Raw <c>fsutil devdrv query</c> output: whether Dev Drives are enabled, AV filter protection, and
    /// the filter attach policy (the per-volume query needs elevation, so the global one is used).
    /// </summary>
    public static string? DevDriveFilterInfo(string devRoot, IWorkloadProcessRunner? runner = null)
    {
        try
        {
            runner ??= new WorkloadProcessRunner();
            ProcessRunResult r = runner.Run(new WorkloadProcessRequest("fsutil.exe", "devdrv query", TimeoutMs: 10_000));
            string text = (r.StandardOutput + r.StandardError).Trim();
            return r.ExitCode != 0 || string.IsNullOrEmpty(text) ? null : text.Replace("\r\n", "\n");
        }
        catch
        {
            return null;
        }
    }

    private static string CpuName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
