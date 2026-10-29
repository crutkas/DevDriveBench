// Ported from crutkas/devdrive (branch crutkas-drive-space-analyzer), MIT License.
namespace DevDriveBench.Engine.Abstractions;

/// <summary>Result of running an external process.</summary>
/// <param name="ExitCode">Process exit code; negative values denote launch failure / timeout.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True when the process was killed because it exceeded the timeout.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// A request to run an external tool for a benchmark, with the working directory, extra environment
/// variables, optional standard input, and a generous timeout.
/// </summary>
/// <param name="FileName">Executable to launch (e.g. <c>git</c>, <c>cmd</c>, <c>dotnet</c>, <c>cargo</c>).</param>
/// <param name="Arguments">Command-line arguments.</param>
/// <param name="WorkingDirectory">Working directory, or <c>null</c> to inherit the current one.</param>
/// <param name="Environment">Extra environment variables to set for the child process (added to the inherited block).</param>
/// <param name="TimeoutMs">Kill the process after this many milliseconds. Defaults to 10 minutes.</param>
public sealed record WorkloadProcessRequest(
    string FileName,
    string Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    int TimeoutMs = 600_000)
{
    /// <summary>Text written to the child's standard input (UTF-8, no BOM), then closed. Null leaves stdin alone.</summary>
    public string? StandardInput { get; init; }
}

/// <summary>
/// Runs external tools for the real-workload benchmarks, with working-directory, environment, and
/// timeout control. Returns the same <see cref="ProcessRunResult"/> shape as <see cref="IProcessRunner"/>.
/// </summary>
/// <remarks>The execution seam for benchmark tools — benchmark unit tests inject a fake so the suite
/// never launches a real process.</remarks>
public interface IWorkloadProcessRunner
{
    /// <summary>Runs the requested process to completion (or timeout) and captures its output.</summary>
    ProcessRunResult Run(WorkloadProcessRequest request, CancellationToken cancellationToken = default);
}
