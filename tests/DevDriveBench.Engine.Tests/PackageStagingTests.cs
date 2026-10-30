using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Platform;

namespace DevDriveBench.Engine.Tests;

public class PackageStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ddb-staging-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void PrepareStagesEveryPairAndRunsReadTheChosenOne()
    {
        (WorkloadEnvironment env, StagingWorkload workload) = Setup();
        workload.Prepare(env, CancellationToken.None);

        string sysForSys = workload.Slot(env.SystemRoot, env.SystemRoot);
        string sysForDev = workload.Slot(env.SystemRoot, env.DevRoot);
        string devForDev = workload.Slot(env.DevRoot, env.DevRoot);
        foreach (string slot in new[] { sysForSys, sysForDev, devForDev })
        {
            Assert.True(File.Exists(Path.Combine(slot, "pkg", "a.txt")), slot);
            Assert.False(Directory.Exists(Path.Combine(slot, "http-cache")), "skipped directory was staged");
        }

        workload.MeasureOnce(env.DevRoot, env.SystemRoot, CancellationToken.None);
        workload.MeasureOnce(env.DevRoot, env.DevRoot, CancellationToken.None);
        Assert.Equal(new[] { sysForDev, devForDev }, workload.CacheDirs);
        Assert.All(workload.WorkDirs, w => Assert.StartsWith(env.DevRoot, w, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RefreshRestagesWhatARunWroteAndCleanupRemovesEverything()
    {
        (WorkloadEnvironment env, StagingWorkload workload) = Setup();
        workload.Prepare(env, CancellationToken.None);
        workload.MeasureOnce(env.DevRoot, env.DevRoot, CancellationToken.None);
        string slot = workload.Slot(env.DevRoot, env.DevRoot);
        Assert.True(File.Exists(Path.Combine(slot, "written-by-build.txt")));

        workload.CleanupDrives();
        workload.RefreshPackages();
        Assert.False(File.Exists(Path.Combine(slot, "written-by-build.txt")));
        Assert.True(File.Exists(Path.Combine(slot, "pkg", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(env.DevRoot, "DevDriveBench.Workload")));

        workload.Cleanup();
        Assert.False(Directory.Exists(Path.Combine(env.SystemRoot, "DevDriveBench.Packages")));
        Assert.False(Directory.Exists(Path.Combine(env.DevRoot, "DevDriveBench.Packages")));
    }

    [Fact]
    public void ReadOnlyCachesAreNotRewrittenBetweenRuns()
    {
        (WorkloadEnvironment env, StagingWorkload workload) = Setup(readOnly: true);
        workload.Prepare(env, CancellationToken.None);
        workload.MeasureOnce(env.DevRoot, env.DevRoot, CancellationToken.None);
        string slot = workload.Slot(env.DevRoot, env.DevRoot);

        workload.RefreshPackages();
        Assert.True(File.Exists(Path.Combine(slot, "written-by-build.txt")));
    }

    [Fact]
    public void AMissingStagedCacheIsStagedBeforeMeasuring()
    {
        (WorkloadEnvironment env, StagingWorkload workload) = Setup();
        workload.Prepare(env, CancellationToken.None);
        string slot = workload.Slot(env.SystemRoot, env.SystemRoot);
        Directory.Delete(slot, recursive: true);

        workload.MeasureOnce(env.SystemRoot, env.SystemRoot, CancellationToken.None);
        Assert.True(workload.SawPackageWhenTimed);
    }

    private (WorkloadEnvironment Env, StagingWorkload Workload) Setup(bool readOnly = false)
    {
        string system = Path.Combine(_root, "sys");
        string dev = Path.Combine(_root, "dev");
        string seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(system);
        Directory.CreateDirectory(dev);
        return (new WorkloadEnvironment(system, dev, seed), new StagingWorkload { ReadOnly = readOnly });
    }

    private sealed class StagingWorkload : WorkloadBenchmarkBase
    {
        private string _seedCache = string.Empty;

        public StagingWorkload() : base(new WorkloadProcessRunner()) { }

        public bool ReadOnly { get; init; }

        protected override bool RunsWritePackages => !ReadOnly;

        public override string Name => "staging";

        public override string Detail => "test";

        public override string RequiredTool => "none";

        public List<string> CacheDirs { get; } = new();

        public List<string> WorkDirs { get; } = new();

        public bool SawPackageWhenTimed { get; private set; }

        protected override string Slug => "staging";

        protected override long MinFreeBytesPerDrive => 1;

        protected override string? PackageSeed => _seedCache;

        public string Slot(string packagesRoot, string codeRoot) => PackageSlot(packagesRoot, codeRoot);

        protected override bool SkipWhenStaging(string directoryName) => directoryName == "http-cache";

        protected override void PrepareCore(WorkloadEnvironment environment, CancellationToken cancellationToken)
        {
            _seedCache = Path.Combine(SeedDirectory, "cache");
            Directory.CreateDirectory(Path.Combine(_seedCache, "pkg"));
            Directory.CreateDirectory(Path.Combine(_seedCache, "http-cache"));
            File.WriteAllText(Path.Combine(_seedCache, "pkg", "a.txt"), "package");
            File.WriteAllText(Path.Combine(_seedCache, "http-cache", "big.dat"), "download");
        }

        protected override double MeasureCore(string driveRoot, string workDir, string cacheDir, CancellationToken cancellationToken)
        {
            CacheDirs.Add(cacheDir);
            WorkDirs.Add(workDir);
            SawPackageWhenTimed = File.Exists(Path.Combine(cacheDir, "pkg", "a.txt"));
            File.WriteAllText(Path.Combine(cacheDir, "written-by-build.txt"), "x");
            return 1;
        }
    }
}
