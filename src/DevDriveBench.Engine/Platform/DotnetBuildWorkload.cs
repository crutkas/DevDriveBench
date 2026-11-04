// Ported from crutkas/devdrive (branch crutkas-drive-space-analyzer), MIT License.
// Fixture changed from Rx.NET (net6.0) to dotnet/eShop on .NET 10.
using DevDriveBench.Engine.Abstractions;

namespace DevDriveBench.Engine.Platform;

/// <summary>
/// Times <c>dotnet build</c> of a real, Microsoft-owned .NET 10 application — <b>dotnet/eShop</b>
/// (the reference microservices + Blazor web app) — pinned to a commit, on each drive.
/// </summary>
/// <remarks>
/// <para>The fixture is the <c>eShop.Web.slnf</c> solution filter (the web app, services, Aspire
/// AppHost and their tests; the MAUI clients are excluded). <see cref="PrepareCore"/> does the one
/// network step: a shallow fetch of the pinned commit and a <c>dotnet restore</c> that warms a seed
/// <c>NUGET_PACKAGES</c> folder, which is then staged onto the drives (see
/// <see cref="WorkloadBenchmarkBase"/>). Each measured iteration copies the source onto the drive under
/// test, re-restores <em>offline</em> against the staged cache on the drive the plan chose (untimed
/// setup), then <b>times</b> <c>dotnet build eShop.Web.slnf -c Release --no-restore</c> — so the
/// <c>artifacts</c> output is on the code drive and the build's package reads are on the packages
/// drive.</para>
/// <para><b>Why an untimed re-restore:</b> <c>dotnet build --no-restore</c> reads packages from the
/// absolute global-packages path baked into the restore output, which <c>NUGET_PACKAGES</c> does not
/// override at build time. So restore output (eShop uses <c>UseArtifactsOutput</c>, i.e.
/// <c>artifacts/</c>) is never copied; each iteration runs a fully offline restore that resolves from
/// the staged cache. NuGet's HTTP download cache is left out of staged copies: offline restores never
/// read it.</para>
/// <para><b>Fair, cold builds:</b> build servers are shut down (untimed, while the next iteration's
/// setup copies files) and must be gone before every timed build, so the Dev Drive run can't inherit
/// MSBuild worker nodes or a warm compiler server left behind by the system-drive run (that would
/// favor whichever drive goes second), while the build
/// still shares its compiler server across projects like a real first build of the day. Restores run
/// with <c>--disable-build-servers</c>. NuGet audit is disabled because it needs the network and
/// eShop treats warnings as errors.</para>
/// <para><b>Best-effort:</b> if git or the network is unavailable the fetch or restore fails and the
/// benchmark skips with a clear reason rather than fabricating a number.</para>
/// </remarks>
public sealed class DotnetBuildWorkload : WorkloadBenchmarkBase
{
    /// <summary>The real, Microsoft-owned application this benchmark builds.</summary>
    private const string RepoUrl = "https://github.com/dotnet/eShop.git";

    /// <summary>Pinned commit (main, .NET 10; global.json SDK 10.0.302, rollForward latestFeature).</summary>
    public const string RepoCommit = "913acea66708a59595351174cee9b29f37179d29";

    /// <summary>The solution filter built: web app + services + AppHost + their tests, no MAUI clients.</summary>
    private const string SolutionFilter = "eShop.Web.slnf";

    private const string CommonArgs = "--nologo --disable-build-servers -p:NuGetAudit=false";

    private const int FetchTimeoutMs = 300_000;
    private const int SeedRestoreTimeoutMs = 900_000;
    private const int RestoreTimeoutMs = 600_000;
    private const int BuildTimeoutMs = 900_000;

    private string _repoSeed = string.Empty;

    // The warm SEED global-packages folder, populated once (with network) in PrepareCore and never
    // measured directly: each iteration copies it cold onto the drive under test. Lives under %TEMP%.
    private string _seedCache = string.Empty;

    /// <summary>Creates the benchmark over a process runner seam.</summary>
    public DotnetBuildWorkload(IWorkloadProcessRunner runner) : base(runner) { }

    /// <inheritdoc />
    public override string Name => "dotnet build";

    /// <inheritdoc />
    public override string Detail =>
        $"dotnet/eShop @{RepoCommit[..7]} \u00B7 {SolutionFilter} (25 projects, .NET 10) \u00B7 Release \u00B7 NUGET_PACKAGES staged at Prepare \u00B7 offline \u00B7 build servers cold per run ({Profile})";

    /// <inheritdoc />
    public override string RequiredTool => "dotnet";

    /// <inheritdoc />
    protected override string Slug => "dotnet-build";

    /// <summary>The eShop seed (source + warm NuGet cache) and per-iteration copies need real headroom.</summary>
    protected override long MinFreeBytesPerDrive => 8L * 1024 * 1024 * 1024;

    /// <inheritdoc />
    protected override void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken)
    {
        _repoSeed = Path.Combine(SeedDirectory, "eShop");
        _seedCache = Path.Combine(SeedDirectory, "nuget");
        Directory.CreateDirectory(_seedCache);

        // Network step 1/2: a shallow fetch of exactly the pinned commit (reproducible, small).
        // `core.longpaths` keeps a long checkout path from masquerading as a network failure.
        RunPrepare(Git("init -q .", _repoSeed), "git init", cancellationToken, FetchTimeoutMs, createDirectory: true);
        RunPrepare(Git($"fetch -q --depth 1 {RepoUrl} {RepoCommit}", _repoSeed), $"could not fetch dotnet/eShop {RepoCommit[..7]}", cancellationToken, FetchTimeoutMs);
        RunPrepare(Git("-c core.longpaths=true checkout -q FETCH_HEAD", _repoSeed), "could not check out dotnet/eShop", cancellationToken, FetchTimeoutMs);

        // eShop clears the configured feeds in favor of nuget.org. Always inherit the user's/machine's
        // feeds instead, without first probing a public endpoint that policy may block. This only
        // affects the one-time seed restore; measured restores still override every feed offline.
        File.WriteAllText(
            Path.Combine(_repoSeed, "nuget.config"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <!-- DevDriveBench: inherit this machine's NuGet feeds. -->\n  <packageSources />\n</configuration>\n");

        // Network step 2/2: warm a seed global-packages folder once. Measured iterations copy it cold
        // onto the test drive and re-restore offline from the copy.
        RunPrepare(
            new WorkloadProcessRequest("dotnet", $"restore {SolutionFilter} {CommonArgs}", _repoSeed, NugetEnvironment(_seedCache), SeedRestoreTimeoutMs),
            "could not restore NuGet packages for dotnet/eShop",
            cancellationToken,
            SeedRestoreTimeoutMs);
    }

    /// <inheritdoc />
    protected override double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken)
    {
        // Servers left by the previous build are shut down while the setup below runs (the offline
        // restore never starts any); the timed build still waits until they are gone. The seed folder
        // carries the same global.json as the copy, so the shutdown targets the same SDK's servers.
        Task shutdown = Task.Run(() => ShutdownBuildServers(_repoSeed, cancellationToken), CancellationToken.None);
        try
        {
            // SETUP (untimed) 1/2: the source, without git metadata or any build/restore output (the
            // offline restore below regenerates it pointing at the staged NuGet cache).
            CopyDirectory(_repoSeed, workDir, skipDirectory: name => name is ".git" or "artifacts" or "bin" or "obj" or "node_modules");

            // SETUP (untimed) 2/2: restore OFFLINE against the staged cache. `--source <empty dir>`
            // overrides every feed, so nothing can come from the network.
            string offlineFeed = Path.Combine(workDir, "_offline-feed");
            Directory.CreateDirectory(offlineFeed);
            RunChecked(
                new WorkloadProcessRequest(
                    "dotnet",
                    $"restore {SolutionFilter} {CommonArgs} --source \"{offlineFeed}\"",
                    workDir,
                    NugetEnvironment(cacheDir),
                    RestoreTimeoutMs),
                cancellationToken,
                "dotnet restore (offline, per-drive cache)");
        }
        catch
        {
            // Setup failed: still wait for the servers to go, but report the setup error.
            try
            {
                shutdown.GetAwaiter().GetResult();
            }
            catch
            {
                // Secondary to the setup failure being reported.
            }

            throw;
        }

        shutdown.GetAwaiter().GetResult();

        // TIMED: the build of the whole solution filter, resolving from the per-drive cache. Each drive's
        // build starts with no warm MSBuild nodes or compiler server inherited from the other drive's run,
        // yet shares its compiler server across projects within the build like a real first build of the
        // day. The servers this build leaves behind are shut down during the next iteration's setup or
        // when the run's drives are cleaned up.
        return TimeProcess(
            new WorkloadProcessRequest(
                "dotnet",
                $"build {SolutionFilter} -c Release --no-restore --nologo -p:NuGetAudit=false",
                workDir,
                NugetEnvironment(cacheDir),
                BuildTimeoutMs),
            cancellationToken,
            "dotnet build");
    }

    /// <inheritdoc />
    protected override string? PackageSeed => _seedCache;

    /// <inheritdoc />
    protected override bool SkipWhenStaging(string directoryName) => directoryName is "http-cache";

    /// <summary>
    /// The timed build only reads <c>NUGET_PACKAGES</c>, and the untimed offline restore finds every package
    /// already there, so the large staged caches stay as staged at Prepare instead of being rewritten
    /// between runs.
    /// </summary>
    protected override bool RunsWritePackages => false;

    /// <inheritdoc />
    protected override void ReleaseResources()
    {
        try
        {
            Runner.Run(new WorkloadProcessRequest("dotnet", "build-server shutdown", null, null, 120_000));
        }
        catch
        {
            // Best-effort: idle servers exit on their own.
        }
    }

    private void ShutdownBuildServers(string workDir, CancellationToken cancellationToken) =>
        Runner.Run(new WorkloadProcessRequest("dotnet", "build-server shutdown", workDir, null, 120_000), cancellationToken);

    private void RunPrepare(WorkloadProcessRequest request, string what, CancellationToken cancellationToken, int timeoutMs, bool createDirectory = false)
    {
        if (createDirectory && request.WorkingDirectory is string dir)
        {
            Directory.CreateDirectory(dir);
        }

        ProcessRunResult result = Runner.Run(request, cancellationToken);
        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new WorkloadUnavailableException($"{what}: {DescribeProcessFailure(result, timeoutMs)}");
        }
    }

    private static WorkloadProcessRequest Git(string arguments, string workingDirectory) =>
        new("git", arguments, workingDirectory, TimeoutMs: FetchTimeoutMs);

    /// <summary>
    /// Reports the actual error and preserves the complete process output, rather than guessing
    /// "no network" or retaining only a success-looking project output line.
    /// </summary>
    private static string DescribeProcessFailure(ProcessRunResult result, int timeoutMs)
    {
        if (result.TimedOut)
        {
            return $"timed out after {timeoutMs / 1000}s{FailureDetails(result)}";
        }

        return $"exit {result.ExitCode}{FailureDetails(result)}";
    }

    private static Dictionary<string, string> NugetEnvironment(string packagesDir)
    {
        // Redirect EVERY NuGet/dotnet cache under the supplied packages dir, not just the global
        // packages folder, so nothing touches (or pollutes) the real user-profile caches and all
        // NuGet/dotnet I/O stays on the drive under test. The tools assume these exist.
        string httpCache = Path.Combine(packagesDir, "http-cache");
        string pluginsCache = Path.Combine(packagesDir, "plugins-cache");
        string scratch = Path.Combine(packagesDir, "scratch");
        string cliHome = Path.Combine(packagesDir, "cli-home");
        Directory.CreateDirectory(httpCache);
        Directory.CreateDirectory(pluginsCache);
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(cliHome);

        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_PACKAGES"] = packagesDir,
            ["NUGET_HTTP_CACHE_PATH"] = httpCache,
            ["NUGET_PLUGINS_CACHE_PATH"] = pluginsCache,
            ["NUGET_SCRATCH"] = scratch,
            ["DOTNET_CLI_HOME"] = cliHome,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",

            // The SDK's background workload-update check would query NuGet feeds and write into the
            // staged cache during setup or the timed build; benchmark runs stay offline and leave the
            // cache as staged. (The SDK parses this one as a bool: "1" is ignored, "true" works.)
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true",
        };
    }
}
