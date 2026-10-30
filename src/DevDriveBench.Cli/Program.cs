using System.Globalization;
using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Platform;
using DevDriveBench.Engine.Run;

// Headless runner for data gathering: same engine, same recordings (replayable in the app).
//   devdrivebench [--runs N] [--profile quick|thorough] [--tests git-clone,disk,...] [--dev D:] [--out DIR] [--prepare] [--packages optimal|c|both] [--list] [--timing FILE]

var options = Args.Parse(args);

// Pick up tools installed since this shell started (PATH is re-read from the registry).
EnvironmentRefresher.RefreshPath();
if (options.Help)
{
    Console.WriteLine("devdrivebench [--runs N] [--profile quick|thorough] [--tests git-clone,disk,npm-ci,dotnet-build,cargo-build] [--dev D:] [--out DIR] [--prepare] [--packages optimal|c|both] [--list] [--timing RECORDING.json]");
    return 0;
}

if (options.Timing is string saved)
{
    // Dead time between measured steps of any saved recording (e.g. compare runs with and without --prepare).
    PrintStage(RecordingStore.Load(saved));
    return 0;
}

IReadOnlyList<DriveDescriptor> drives = DriveProbe.GetFixedDrives();
if (options.List)
{
    foreach (DriveDescriptor d in drives)
    {
        Console.WriteLine($"{d.Letter,-3} {d.FileSystem,-5} {(d.IsSystem ? "system " : "")}{(d.IsDevDrive ? (d.IsTrusted ? "dev-drive(trusted) " : "dev-drive ") : "")}{d.BusType} {d.DiskModel} {d.BackingFile}");
    }

    Console.WriteLine();
    var probe = new ToolProbe();
    string os = MachineProbe.Capture().OsArchitecture;
    foreach (string tool in new[] { "git", "npm", "dotnet", "cargo" })
    {
        ToolInfo t = probe.Detect(tool);
        string flag = t.Architecture is string a && a != os ? $"  (emulated on {os})" : string.Empty;
        Console.WriteLine(t.Found ? $"{tool,-7} {t.Version,-10} {t.Architecture ?? "?",-6} {t.Binary}{flag}" : $"{tool,-7} not installed");
    }

    return 0;
}

DriveDescriptor? system = drives.FirstOrDefault(d => d.IsSystem);
DriveDescriptor? dev = options.Dev is string letter
    ? drives.FirstOrDefault(d => d.Letter.Equals(letter.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
    : drives.FirstOrDefault(d => d.IsDevDrive);
if (system is null || dev is null)
{
    Console.Error.WriteLine("No Dev Drive found (use --dev X: to pick a volume).");
    return 2;
}

var runner = BenchRunner.CreateDefault();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Counter setup costs seconds once per process; pay it before the clock starts so the activity trace
// covers the whole run.
PerfCounterActivitySource.Warm();

var plan = new BenchPlan
{
    SystemDrive = system,
    DevDrive = dev,
    Iterations = options.Runs,
    Profile = options.Profile,
    Groups = options.Tests,
};

Console.WriteLine($"{system.Letter} {system.FileSystem}  vs  {dev.Letter} {dev.FileSystem}{(dev.IsDevDrive ? " Dev Drive" : "")}{(dev.IsVirtual ? " (virtual disk)" : "")}  ·  {plan.Iterations} run(s)  ·  {plan.Profile}");

RunRecording? preparation = null;
if (options.Prepare)
{
    // Stage flow: prepare everything first, then measure with no setup waits in between.
    preparation = await runner.PrepareAsync(plan, Print, cancellationToken: cts.Token);
    Console.WriteLine($"  prepared in {preparation.DurationSeconds:0.0}s ({preparation.Outcome})\n");
}

// --packages both: "C drive" first (the Dev Drive run reads packages from C:), then "optimal drive" (each
// run's packages on its own drive) with the first run kept next to it: moving the packages is the second jump.
PackageLocation[] locations = options.Packages switch
{
    "system" => new[] { PackageLocation.SystemDrive },
    "both" => new[] { PackageLocation.SystemDrive, PackageLocation.DevDrive },
    _ => new[] { PackageLocation.DevDrive },
};

RunRecording? previous = null;
RunRecording recording = null!;
foreach (PackageLocation packages in locations)
{
    Console.WriteLine(packages == PackageLocation.SystemDrive
        ? $"  packages: {system.Letter.TrimEnd(':')} drive (the {dev.Letter} run reads npm_config_cache, NUGET_PACKAGES, CARGO_HOME from {system.Letter}; this process only)"
        : $"  packages: optimal drive (each run's npm_config_cache, NUGET_PACKAGES, CARGO_HOME on its own drive; this process only)");
    var wall = System.Diagnostics.Stopwatch.StartNew();
    recording = await runner.RunAsync(plan with { Packages = packages }, Print, cancellationToken: cts.Token);
    double finishing = wall.Elapsed.TotalSeconds - recording.DurationSeconds;
    if (preparation is not null && recording.Info.PreparedAhead)
    {
        recording = recording with { Preparation = preparation };
    }

    if (previous is not null && ComparisonRun.Pairs(recording.Info, previous.Info))
    {
        recording = recording with { Comparison = ComparisonRun.From(previous) };
    }

    Console.WriteLine();
    foreach (TestResult r in recording.Results)
    {
        string line = r.Status == TestStatus.Skipped
            ? $"  {r.Name,-18} skipped: {r.SkipReason}"
            : $"  {r.Name,-18} {Fmt(r.SystemValue, r.Unit),12}  {Fmt(r.DevValue, r.Unit),12}  {(r.Advantage is double a ? a.ToString("0.00", CultureInfo.InvariantCulture) + "x" : "")}";
        Console.WriteLine(line);
    }

    if (recording.Headline is double h)
    {
        Console.WriteLine($"\n  Dev Drive workloads: {h.ToString("0.00", CultureInfo.InvariantCulture)}x faster (median)");
    }

    PrintStage(recording);
    Console.WriteLine($"  after the result: {finishing:0.0}s deleting run files and re-staging packages (off the measured path)");

    string path = RecordingStore.Save(recording, options.Out);
    Console.WriteLine($"\n  {recording.Outcome} in {recording.DurationSeconds:0.0}s -> {path}\n");
    if (recording.Outcome != RunOutcome.Completed)
    {
        break;
    }

    previous = recording;
}

runner.ReleasePrepared();

if (recording.Comparison is ComparisonRun other)
{
    // Three scenarios per workload: everything on C:, code on the Dev Drive with packages on C:, everything on the Dev Drive.
    Console.WriteLine($"  {"",-18} {system.Letter + " code+pkgs",14} {dev.Letter + " code, " + system.Letter + " pkgs",16} {dev.Letter + " code+pkgs",14}");
    foreach (TestResult r in recording.Results.Where(r => TestCatalog.PackageVariable(r.TestId) is not null && r.Status != TestStatus.Skipped))
    {
        TestResult? o = other.Results.FirstOrDefault(x => x.TestId == r.TestId);
        Console.WriteLine($"  {r.Name,-18} {Fmt(r.SystemValue, r.Unit),14} {Fmt(o?.DevValue, r.Unit),16} {Fmt(r.DevValue, r.Unit),14}");
    }

    Console.WriteLine();
}

return recording.Outcome == RunOutcome.Completed ? 0 : 1;

static void PrintStage(RunRecording recording)
{
    StageTiming timing = StageTiming.From(recording.Events);
    Console.WriteLine($"\n  stage: {timing.Total:0.0}s total · {timing.Measured:0.0}s measured · {timing.DeadTime:0.0}s between");
    foreach (TimingGap gap in timing.Gaps.Where(g => g.Seconds >= 0.5))
    {
        Console.WriteLine($"    {gap.Seconds,5:0.0}s  {gap.After} -> {gap.Before}");
    }
}

static void Print(BenchEvent e)
{
    string? line = e switch
    {
        PhaseStartedEvent p when p.Phase is RunPhase.Prepare or RunPhase.Cleanup => $"{e.T,8:0.0}  {p.TestId} {p.Phase.ToString().ToLowerInvariant()}",
        PhaseStartedEvent p when p.Phase == RunPhase.Measure && !p.TestId.StartsWith("disk", StringComparison.Ordinal) => $"{e.T,8:0.0}  {p.TestId} {p.Drive} run {p.Iteration}",
        MeasurementEvent m => $"{e.T,8:0.0}  {m.TestId} {m.Drive} run {m.Iteration} = {m.Value:0.###}",
        TestSkippedEvent s => $"{e.T,8:0.0}  {s.TestId} skipped: {s.Reason}",
        TestPreparedEvent p when !p.TestId.StartsWith("disk", StringComparison.Ordinal) => $"{e.T,8:0.0}  {p.TestId} {(p.Reused ? "already prepared" : $"prepared in {p.Seconds:0.0}s")}",
        _ => null,
    };
    if (line is not null)
    {
        Console.WriteLine(line);
    }
}

static string Fmt(double? value, string unit) => value is double v
    ? unit == "s" ? $"{v:0.00} s" : unit == "IOPS" ? $"{v:N0} IOPS" : $"{v:N0} {unit}"
    : "-";

internal sealed record Args
{
    public int Runs { get; init; } = 1;
    public WorkloadProfile Profile { get; init; } = WorkloadProfile.Thorough;
    public IReadOnlyCollection<string>? Tests { get; init; }
    public string? Dev { get; init; }
    public string? Out { get; init; }
    public bool List { get; init; }
    public bool Prepare { get; init; }
    public string Packages { get; init; } = "dev";
    public string? Timing { get; init; }
    public bool Help { get; init; }

    public static Args Parse(string[] args)
    {
        var a = new Args();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : string.Empty;
            a = args[i].ToLowerInvariant() switch
            {
                "--runs" or "-n" => a with { Runs = Math.Max(1, int.TryParse(Next(), out int n) ? n : 1) },
                "--profile" => a with { Profile = Next().StartsWith("q", StringComparison.OrdinalIgnoreCase) ? WorkloadProfile.Quick : WorkloadProfile.Thorough },
                "--tests" => a with { Tests = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
                "--dev" => a with { Dev = Next() },
                "--out" => a with { Out = Next() },
                "--list" => a with { List = true },
                "--prepare" => a with { Prepare = true },
                "--packages" => Next().ToLowerInvariant() switch
                {
                    "both" => a with { Packages = "both" },
                    "c" or "c:" or "system" or "sys" => a with { Packages = "system" },
                    "optimal" or "dev" or "devdrive" => a with { Packages = "dev" },
                    _ => a with { Help = true },
                },
                "--timing" => a with { Timing = Next() },
                _ => a with { Help = true },
            };
        }

        return a;
    }
}
