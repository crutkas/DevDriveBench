// Ported from crutkas/devdrive (branch crutkas-drive-space-analyzer), MIT License.
using System.Diagnostics;
using System.Text.RegularExpressions;
using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Model;

namespace DevDriveBench.Engine.Platform;

/// <summary>
/// Shared scaffolding for the real-workload benchmarks: bounded temp fixtures, a per-drive working
/// area, package caches staged per drive at Prepare (re-staged after each run), and robust cleanup.
/// </summary>
/// <remarks>
/// <para><b>SAFETY:</b> every directory the benchmark creates is tracked and deleted in
/// <see cref="Cleanup"/> (called from the orchestrator's <c>finally</c>), and each measured iteration
/// deletes its own working copy. Fixtures are small and a free-space guard skips the benchmark when a
/// drive is low. No machine-config, env-var, or partition/format changes are ever made — only files
/// under the seed (<c>%TEMP%</c>) and per-drive <c>DevDriveBench.Workload</c> / <c>DevDriveBench.Packages</c>
/// folders. Package locations reach the tools only through environment variables on the benchmark's
/// own child processes; the user's and machine's variables are never changed.</para>
/// <para><b>F9 (ownership):</b> a per-drive bench <c>&lt;Slug&gt;</c> folder is only ever deleted
/// wholesale when this benchmark created it (stamping a <see cref="BenchOwnershipMarkerFileName"/>
/// receipt) or a prior run left that receipt. A predictable pre-existing bench folder that lacks the
/// marker is treated as foreign user data: it is never recursively deleted — only the sub-directories
/// the benchmark itself creates under it are removed, and the folder is dropped only if it ends up
/// empty.</para>
/// </remarks>
public abstract class WorkloadBenchmarkBase : IWorkloadBenchmark
{
    // F11: directories THIS benchmark created and may delete wholesale, split by lifetime: the seed
    // (kept between runs once prepared) and the per-drive bench folders (deleted after every run).
    private readonly HashSet<string> _seedOwned = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _driveOwned = new(StringComparer.OrdinalIgnoreCase);

    // F11: shared FIXED PARENTS (e.g. the per-drive bench root and the %TEMP% seed root) that sibling
    // benchmarks also use. These are NEVER deleted wholesale — only removed if they end up empty.
    private readonly HashSet<string> _seedParents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _driveParents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _trackGate = new();

    // Staged package caches that are complete copies of the seed, and the (packages|code) pairs runs
    // have read since the last RefreshPackages.
    private readonly HashSet<string> _staged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// F9: small ownership receipt dropped into a per-drive bench <c>&lt;Slug&gt;</c> directory this
    /// benchmark creates. Its presence marks the folder as app-managed, so a later run (or
    /// <see cref="Cleanup"/>) may delete it wholesale; a predictable pre-existing bench folder WITHOUT it
    /// is treated as foreign user data and is never recursively deleted.
    /// </summary>
    public const string BenchOwnershipMarkerFileName = ".devdrivebench-bench";

    /// <summary>Creates the benchmark over a process runner seam.</summary>
    protected WorkloadBenchmarkBase(IWorkloadProcessRunner runner) =>
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>The process runner used to launch tools.</summary>
    protected IWorkloadProcessRunner Runner { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Detail { get; }

    /// <inheritdoc />
    public WorkloadProfile Profile { get; set; } = WorkloadProfile.Quick;

    /// <inheritdoc />
    public Action? TimedSectionStarting { get; set; }

    /// <inheritdoc />
    public Action<double>? TimedSectionCompleted { get; set; }

    /// <inheritdoc />
    public abstract string RequiredTool { get; }

    /// <summary>Folder-safe identifier used for this benchmark's fixture/working folders.</summary>
    protected abstract string Slug { get; }

    /// <summary>Minimum free space required on each drive; below this the benchmark skips (default 2 GiB).</summary>
    protected virtual long MinFreeBytesPerDrive => 2L * 1024 * 1024 * 1024;

    /// <summary>The shared seed folder for this benchmark (under <c>%TEMP%</c>), set in <see cref="Prepare"/>.</summary>
    protected string SeedDirectory { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Prepare(WorkloadEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();

        GuardFreeSpace(environment.SystemRoot);
        GuardFreeSpace(environment.DevRoot);

        SeedDirectory = Path.Combine(environment.SeedRoot, Slug);
        ResetDirectory(SeedDirectory);

        // F11: this benchmark owns only its own Slug subfolder. The shared seed root is a FIXED PARENT
        // that sibling benchmarks also stage fixtures under, so never delete it wholesale — at Cleanup
        // remove it only if it has become empty.
        TrackParent(environment.SeedRoot, seed: true);
        Track(SeedDirectory, seed: true);

        PrepareCore(environment, cancellationToken);
        StagePackages(environment, cancellationToken);
    }

    /// <summary>
    /// The warm package cache this workload's runs read (set by <see cref="PrepareCore"/>), or null when
    /// the workload has none. It is staged onto the drives at Prepare and re-staged after each run.
    /// </summary>
    protected virtual string? PackageSeed => null;

    /// <summary>Directory names inside <see cref="PackageSeed"/> that runs never read (left out of staged copies).</summary>
    protected virtual bool SkipWhenStaging(string directoryName) => false;

    /// <summary>
    /// True when a run can change the staged cache (e.g. cargo unpacks crates into <c>CARGO_HOME</c>), so it
    /// is re-staged after every run that used it. Workloads whose runs only read the cache return false:
    /// their caches stay as staged at Prepare, like a machine's long-lived package cache, and nothing is
    /// rewritten between runs.
    /// </summary>
    protected virtual bool RunsWritePackages => true;

    /// <summary>
    /// The staged package cache for code on <paramref name="codeRoot"/> with packages on
    /// <paramref name="packagesRoot"/>, e.g. <c>C:\DevDriveBench.Packages\npm-ci\for-D</c>.
    /// </summary>
    protected string PackageSlot(string packagesRoot, string codeRoot) =>
        Path.Combine(packagesRoot, WorkloadPaths.PackagesFolderName, Slug, "for-" + RootName(codeRoot));

    /// <summary>
    /// Stages the package cache for every pair a run can use: the system-drive run (packages on the
    /// system drive) and the Dev Drive run with packages on either drive. Each pair gets its own copy,
    /// so no run reads a cache another scenario has already warmed.
    /// </summary>
    private void StagePackages(WorkloadEnvironment environment, CancellationToken cancellationToken)
    {
        if (PackageSeed is null)
        {
            return;
        }

        var pairs = new[]
        {
            (Code: environment.SystemRoot, Packages: environment.SystemRoot),
            (Code: environment.DevRoot, Packages: environment.SystemRoot),
            (Code: environment.DevRoot, Packages: environment.DevRoot),
        };
        StageSlots(pairs.Distinct().Select(p => (p.Packages, p.Code)).ToArray(), cancellationToken);
    }

    /// <summary>
    /// (Re)creates staged caches from the seed: ownership is claimed one at a time, then the copies run in
    /// parallel (the slots are independent trees). Throws on failure, leaving the failed slots unstaged.
    /// </summary>
    private void StageSlots(IReadOnlyList<(string PackagesRoot, string CodeRoot)> pairs, CancellationToken cancellationToken)
    {
        string seed = PackageSeed ?? throw new InvalidOperationException("no package seed");
        string[] slots = pairs.Select(p => ClaimSlot(p.PackagesRoot, p.CodeRoot)).ToArray();
        Parallel.ForEach(slots, new ParallelOptions { CancellationToken = cancellationToken }, slot =>
        {
            ResetDirectory(slot);
            CopyDirectory(seed, slot, SkipWhenStaging);
            lock (_trackGate)
            {
                _staged.Add(slot);
            }
        });
    }

    /// <summary>Marks a slot unstaged and records its folders for cleanup (same ownership rules as the bench folders).</summary>
    private string ClaimSlot(string packagesRoot, string codeRoot)
    {
        string top = Path.Combine(packagesRoot, WorkloadPaths.PackagesFolderName);
        string slugDir = Path.Combine(top, Slug);
        string slot = PackageSlot(packagesRoot, codeRoot);

        TrackParent(top, seed: true);
        if (TryClaimOwnedDirectory(slugDir))
        {
            Track(slugDir, seed: true);
        }
        else
        {
            TrackParent(slugDir, seed: true);
            Track(slot, seed: true);
        }

        lock (_trackGate)
        {
            _staged.Remove(slot);
        }

        return slot;
    }

    /// <inheritdoc />
    public void RefreshPackages()
    {
        (string PackagesRoot, string CodeRoot)[] used;
        lock (_trackGate)
        {
            used = _used.Select(k => k.Split('|')).Select(p => (p[0], p[1])).ToArray();
            _used.Clear();
        }

        if (used.Length == 0 || PackageSeed is not string seed || !Directory.Exists(seed))
        {
            return;
        }

        try
        {
            StageSlots(used, CancellationToken.None);
        }
        catch
        {
            // Whatever didn't finish is left unstaged: its next use stages it (untimed) before measuring.
            foreach ((string packagesRoot, string codeRoot) in used)
            {
                string slot = PackageSlot(packagesRoot, codeRoot);
                bool staged;
                lock (_trackGate)
                {
                    staged = _staged.Contains(slot);
                }

                if (!staged)
                {
                    DeleteTree(slot);
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is copied into the package cache or deleted on the critical path: the cache is the copy
    /// staged at Prepare (or re-staged after the last run), and the iteration's working copy is a fresh,
    /// uniquely named directory removed by <see cref="CleanupDrives"/> after the run (or right away if
    /// the drive runs low on space).
    /// </remarks>
    public double MeasureOnce(string driveRoot, string packagesRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagesRoot);
        cancellationToken.ThrowIfCancellationRequested();

        string benchTop = Path.Combine(driveRoot, WorkloadPaths.BenchFolderName);
        string benchRoot = Path.Combine(benchTop, Slug);

        // Deferred deletes must never fill a drive: if this one is getting low, clear what earlier
        // iterations left behind before setting up the next one (rare; costs time only when needed).
        if (FreeBytes(driveRoot) is long free && free < MinFreeBytesPerDrive)
        {
            CleanupDrives();
        }

        // F9: only ever wholesale-delete benchRoot when THIS app created it or a prior run left our
        // ownership marker. A predictable pre-existing benchRoot WITHOUT the marker is foreign user data:
        // treat it as a fixed parent (removed only if empty) and confine wholesale deletion to the
        // sub-directories we create under it (each iteration's cache + run dir).
        bool ownsBenchRoot = TryClaimOwnedDirectory(benchRoot);

        // Each iteration's code and build output go in a NEW directory on the drive under test.
        string workDir = Path.Combine(benchRoot, "run-" + Guid.NewGuid().ToString("N"));

        // F11: benchTop is shared by sibling benchmarks, so it is a FIXED PARENT we never delete wholesale
        // (removed only if empty).
        TrackParent(benchTop, seed: false);
        if (ownsBenchRoot)
        {
            Track(benchRoot, seed: false); // app-owned (we created it / it carries our marker): safe to delete wholesale.
        }
        else
        {
            // F9: foreign pre-existing bench dir — never recurse-delete it. Remove it only if it becomes
            // empty, and wholesale-delete only the sub-directories we own under it.
            TrackParent(benchRoot, seed: false);
            Track(workDir, seed: false);
        }

        Directory.CreateDirectory(workDir);
        string cacheDir = PackageSeed is null ? string.Empty : StagedCache(packagesRoot, driveRoot);
        return MeasureCore(driveRoot, workDir, cacheDir, cancellationToken);
    }

    /// <summary>
    /// The staged cache for this pair, marked as used so <see cref="RefreshPackages"/> re-stages it after
    /// the run. Staged now (untimed) only if Prepare's copy is missing.
    /// </summary>
    private string StagedCache(string packagesRoot, string codeRoot)
    {
        string slot = PackageSlot(packagesRoot, codeRoot);
        bool staged;
        lock (_trackGate)
        {
            staged = _staged.Contains(slot) && Directory.Exists(slot);
        }

        if (!staged)
        {
            StageSlots(new[] { (packagesRoot, codeRoot) }, CancellationToken.None);
        }

        if (RunsWritePackages)
        {
            lock (_trackGate)
            {
                _used.Add(packagesRoot + "|" + codeRoot);
            }
        }

        return slot;
    }

    private static string RootName(string root)
    {
        string name = new(Path.GetFullPath(root).Where(char.IsLetterOrDigit).ToArray());
        return name.Length == 0 ? "root" : name.Length > 40 ? name[^40..] : name;
    }

    /// <inheritdoc />
    public void CleanupDrives()
    {
        ReleaseResources();
        DeleteTracked(seed: false);
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        ReleaseResources();
        lock (_trackGate)
        {
            _staged.Clear();
            _used.Clear();
        }

        DeleteTracked(seed: false);
        DeleteTracked(seed: true);
    }

    /// <summary>Releases anything a workload keeps running between measurements (e.g. build servers). Must not throw.</summary>
    protected virtual void ReleaseResources()
    {
    }

    private void DeleteTracked(bool seed)
    {
        string[] owned;
        string[] parents;
        lock (_trackGate)
        {
            HashSet<string> ownedSet = seed ? _seedOwned : _driveOwned;
            HashSet<string> parentSet = seed ? _seedParents : _driveParents;
            owned = ownedSet.OrderByDescending(d => d.Length).ToArray();
            parents = parentSet.OrderByDescending(d => d.Length).ToArray();
            ownedSet.Clear();
            parentSet.Clear();
        }

        // Delete the directories THIS benchmark owns (deepest first so parents are empty when removed).
        foreach (string dir in owned)
        {
            DeleteTree(dir);
        }

        // F11: shared FIXED PARENTS may still hold a sibling benchmark's fixtures — remove each only if it
        // is now empty, never wholesale, so we never destroy another benchmark's (or pre-existing) data.
        foreach (string parent in parents)
        {
            TryRemoveIfEmpty(parent);
        }
    }

    private static long? FreeBytes(string root)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Seeds the bounded fixture(s) once. Throw <see cref="WorkloadUnavailableException"/> to skip.</summary>
    protected abstract void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one timed iteration with code on <paramref name="driveRoot"/>. <paramref name="workDir"/> is a
    /// fresh per-iteration directory on that drive; <paramref name="cacheDir"/> is the staged package
    /// cache for this run (on the drive the plan put packages on; empty when the workload has no
    /// <see cref="PackageSeed"/>). Point the tool at it through its cache environment variable, for the
    /// child process only. Both are removed at <see cref="Cleanup"/>.
    /// </summary>
    protected abstract double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken);

    /// <summary>Records a directory this benchmark may delete wholesale: with the seed, or with the per-drive run data.</summary>
    private void Track(string directory, bool seed)
    {
        if (!string.IsNullOrEmpty(directory))
        {
            lock (_trackGate)
            {
                (seed ? _seedOwned : _driveOwned).Add(directory);
            }
        }
    }

    /// <summary>
    /// F11: records a shared FIXED PARENT directory. At cleanup it is removed only if it has become
    /// empty (never deleted wholesale), so a sibling benchmark's fixtures are never destroyed.
    /// </summary>
    private void TrackParent(string directory, bool seed)
    {
        if (!string.IsNullOrEmpty(directory))
        {
            lock (_trackGate)
            {
                (seed ? _seedParents : _driveParents).Add(directory);
            }
        }
    }

    /// <summary>
    /// F9: claims <paramref name="directory"/> as app-owned (safe to wholesale-delete) when this benchmark
    /// is creating it now — stamping a <see cref="BenchOwnershipMarkerFileName"/> receipt — or when a prior
    /// run already left that receipt. Returns <c>false</c> for a predictable pre-existing directory that
    /// lacks the marker: foreign data the benchmark must never recursively delete. Best-effort — if the
    /// marker cannot be written, a later run safely degrades to treating the directory as foreign.
    /// </summary>
    private bool TryClaimOwnedDirectory(string directory)
    {
        string markerPath = Path.Combine(directory, BenchOwnershipMarkerFileName);
        if (Directory.Exists(directory))
        {
            // Pre-existing: app-owned only if a prior run left our marker; otherwise it is foreign.
            return File.Exists(markerPath);
        }

        // We are creating it now — stamp the marker so a later run (and this run's Cleanup) recognise it
        // as app-owned and may delete it wholesale.
        Directory.CreateDirectory(directory);
        TryWriteMarker(markerPath);
        return true;
    }

    /// <summary>Best-effort write of the bench ownership marker; never throws.</summary>
    private static void TryWriteMarker(string markerPath)
    {
        try
        {
            File.WriteAllText(
                markerPath,
                "Created by DevDriveBench's workload benchmark. This folder is temporary and is removed "
                    + "automatically after benchmarking. Delete this marker only if you want the app to "
                    + "leave the folder in place.");
        }
        catch
        {
            // Best-effort: if the marker can't be written, a later run treats the dir as foreign (safer default).
        }
    }

    /// <summary>Removes a directory only when it contains no entries; never throws.</summary>
    private static void TryRemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch
        {
            // Best-effort; a concurrent run may be writing under it — leave it in place.
        }
    }

    /// <summary>Runs a process, throwing <see cref="WorkloadUnavailableException"/> on failure, and returns its wall-clock seconds.</summary>
    protected double TimeProcess(WorkloadProcessRequest request, CancellationToken cancellationToken, string what)
    {
        TimedSectionStarting?.Invoke();
        var stopwatch = Stopwatch.StartNew();
        ProcessRunResult result = Runner.Run(request, cancellationToken);
        stopwatch.Stop();
        EnsureSuccess(result, what);
        TimedSectionCompleted?.Invoke(stopwatch.Elapsed.TotalSeconds);
        return stopwatch.Elapsed.TotalSeconds;
    }

    /// <summary>Runs a (non-measured) process, throwing <see cref="WorkloadUnavailableException"/> on failure.</summary>
    protected ProcessRunResult RunChecked(WorkloadProcessRequest request, CancellationToken cancellationToken, string what)
    {
        ProcessRunResult result = Runner.Run(request, cancellationToken);
        EnsureSuccess(result, what);
        return result;
    }

    /// <summary>Throws <see cref="WorkloadUnavailableException"/> when a process did not run, timed out, or exited non-zero.</summary>
    protected static void EnsureSuccess(ProcessRunResult? result, string what)
    {
        if (result is null)
        {
            throw new WorkloadUnavailableException($"{what} did not run");
        }

        if (result.TimedOut)
        {
            throw new WorkloadUnavailableException($"{what} timed out{FailureDetails(result)}");
        }

        if (result.ExitCode != 0)
        {
            throw new WorkloadUnavailableException($"{what} failed (exit {result.ExitCode}){FailureDetails(result)}");
        }
    }

    /// <summary>Guards a drive's free space, skipping the benchmark when it is below <see cref="MinFreeBytesPerDrive"/>.</summary>
    protected void GuardFreeSpace(string root)
    {
        try
        {
            string? normalized = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            var drive = new DriveInfo(normalized);
            if (drive.IsReady && drive.AvailableFreeSpace < MinFreeBytesPerDrive)
            {
                throw new WorkloadUnavailableException(
                    $"not enough free space on {normalized} (needs about {MinFreeBytesPerDrive / (1024 * 1024 * 1024)} GB)");
            }
        }
        catch (WorkloadUnavailableException)
        {
            throw;
        }
        catch
        {
            // DriveInfo can throw on exotic roots; proceed best-effort.
        }
    }

    /// <summary>Deletes a directory and recreates it empty.</summary>
    protected static void ResetDirectory(string directory)
    {
        DeleteTree(directory);
        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Recursively copies <paramref name="source"/> into <paramref name="destination"/>, optionally
    /// skipping directories by name. Directories are created first, then files are copied in parallel
    /// (thousands of small cache files copy several times faster that way); content and timestamps are
    /// identical to a sequential copy, so the result is the same cold, just-written tree.
    /// </summary>
    protected static void CopyDirectory(string source, string destination, Func<string, bool>? skipDirectory = null)
    {
        var files = new List<(string From, string To)>();
        var pending = new Stack<(string From, string To)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            (string from, string to) = pending.Pop();
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from))
            {
                files.Add((file, Path.Combine(to, Path.GetFileName(file))));
            }

            foreach (string sub in Directory.EnumerateDirectories(from))
            {
                string name = Path.GetFileName(sub);
                if (skipDirectory is null || !skipDirectory(name))
                {
                    pending.Push((sub, Path.Combine(to, name)));
                }
            }
        }

        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = workers }, f => File.Copy(f.From, f.To, overwrite: true));
    }

    /// <summary>Robustly deletes a directory tree, clearing read-only attributes (git pack files) only if a plain delete fails, then retrying once. Never throws.</summary>
    protected static void DeleteTree(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                // Clearing attributes walks every file, so pay for it only when a plain delete hit one.
                if (attempt > 0)
                {
                    ClearReadOnly(directory);
                }

                Directory.Delete(directory, recursive: true);
                return;
            }
            catch
            {
                // Retry once after clearing attributes; then give up (fixtures are bounded and in temp/bench areas).
            }
        }
    }

    private static void ClearReadOnly(string directory)
    {
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                    // Best-effort.
                }
            }
        }
        catch
        {
            // Enumeration can fail mid-delete; ignore.
        }
    }

    /// <summary>Keeps an actionable first line and the complete captured output for recordings and tooltips.</summary>
    protected static string FailureDetails(ProcessRunResult result)
    {
        string stderr = result.StandardError.Trim();
        string stdout = result.StandardOutput.Trim();
        string[] lines = (stderr + "\n" + stdout)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        // MSBuild normally writes errors to stdout, after successful project output. Do not let an
        // earlier success line (or a warning on stderr) hide the actual build failure.
        const string errorPattern = @"\berror(?:\s+[A-Za-z]+\d+)?\s*:";
        string summary = lines.FirstOrDefault(line =>
            Regex.IsMatch(line, errorPattern, RegexOptions.IgnoreCase)) ?? lines[0];
        const int summaryLimit = 240;
        if (summary.Length > summaryLimit)
        {
            // A long project path can otherwise consume the entire summary before the error text.
            Match error = Regex.Match(summary, errorPattern, RegexOptions.IgnoreCase);
            if (error.Success && error.Index > 0)
            {
                summary = summary[error.Index..];
            }

            if (summary.Length > summaryLimit)
            {
                summary = summary[..summaryLimit] + "…";
            }
        }

        string details = string.IsNullOrEmpty(stderr) ? string.Empty : $"\nStandard error:\n{stderr}";
        if (!string.IsNullOrEmpty(stdout))
        {
            details += $"\nStandard output:\n{stdout}";
        }

        return $": {summary}{details}";
    }
}
