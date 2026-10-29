using System.Runtime.InteropServices;
using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Model;
using Microsoft.Win32;

namespace DevDriveBench.Engine.Platform;

/// <summary>An installed developer tool, resolved on PATH.</summary>
public sealed record ToolInfo(string Name, bool Found, string? Version, string? Path);

/// <summary>Detects whether a workload's tool is installed (read-only: PATH lookup + version probe).</summary>
public interface IToolProbe
{
    ToolInfo Detect(string tool);
}

/// <summary>Real <see cref="IToolProbe"/>: PATH + PATHEXT resolution and a <c>--version</c> probe.</summary>
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
        return new ToolInfo(tool, true, version, path);
    }

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
