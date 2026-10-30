using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevDriveBench.Engine.Model;

namespace DevDriveBench.Engine.Run;

/// <summary>
/// A complete run: context, the verbatim event stream (raw per-iteration data, live samples, activity),
/// and a derived summary. Replay re-applies <see cref="Events"/> at their original timestamps.
/// </summary>
public sealed record RunRecording
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public RunInfo Info { get; init; } = new();

    public RunOutcome Outcome { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>Median workload advantage (×), or null when no workload produced both values.</summary>
    public double? Headline { get; init; }

    public IReadOnlyList<TestResult> Results { get; init; } = Array.Empty<TestResult>();

    public IReadOnlyList<BenchEvent> Events { get; init; } = Array.Empty<BenchEvent>();

    /// <summary>Builds a recording by folding <paramref name="events"/>.</summary>
    public static RunRecording FromEvents(IReadOnlyList<BenchEvent> events)
    {
        var state = new RunState();
        foreach (BenchEvent e in events)
        {
            state.Apply(e);
        }

        return new RunRecording
        {
            Info = state.Info ?? new RunInfo(),
            Outcome = state.Outcome ?? RunOutcome.Failed,
            DurationSeconds = events.Count == 0 ? 0 : events[^1].T,
            Headline = state.Headline,
            Results = state.Results(),
            Events = events,
        };
    }
}

/// <summary>Light listing entry for the Replay picker.</summary>
public sealed record RecordingSummary(
    string Path,
    DateTimeOffset StartedAt,
    string Machine,
    string SystemDrive,
    string DevDrive,
    int Iterations,
    double DurationSeconds,
    RunOutcome Outcome,
    double? Headline,
    bool IsBundled);

/// <summary>Saves and loads recordings: one replayable JSON plus a flat CSV of raw iterations per run.</summary>
public static class RecordingStore
{
    /// <summary>Serializer settings shared by save/load (camelCase, enums as strings, compact).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    /// <summary>Default folder: <c>Documents\DevDriveBench\Runs</c> (outside any app container, easy to find).</summary>
    public static string DefaultDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DevDriveBench", "Runs");

    /// <summary>Writes <c>.json</c> + <c>.csv</c> and returns the JSON path.</summary>
    public static string Save(RunRecording recording, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        string dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);

        string stem = FileStem(recording);
        string jsonPath = System.IO.Path.Combine(dir, stem + ".json");
        for (int n = 2; File.Exists(jsonPath); n++)
        {
            jsonPath = System.IO.Path.Combine(dir, $"{stem}-{n}.json");
        }

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(recording, JsonOptions), Encoding.UTF8);
        File.WriteAllText(System.IO.Path.ChangeExtension(jsonPath, ".csv"), ToCsv(recording), Encoding.UTF8);
        return jsonPath;
    }

    public static RunRecording Load(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RunRecording>(json, JsonOptions)
            ?? throw new InvalidDataException($"'{path}' is not a DevDriveBench recording.");
    }

    /// <summary>Lists readable recordings in the given folders, newest first. Unreadable files are ignored.</summary>
    public static IReadOnlyList<RecordingSummary> List(string? userDirectory, string? bundledDirectory)
    {
        var list = new List<RecordingSummary>();
        AddFrom(list, userDirectory, bundled: false);
        AddFrom(list, bundledDirectory, bundled: true);
        return list.OrderBy(s => s.IsBundled).ThenByDescending(s => s.StartedAt).ToArray();
    }

    /// <summary>One row per raw iteration — the data-gathering view (first run flagged when discarded).</summary>
    public static string ToCsv(RunRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        RunInfo info = recording.Info;
        var sb = new StringBuilder();
        sb.AppendLine("run_started,machine,cpu,os,profile,iterations,test_id,test,unit,better,drive_role,drive,file_system,dev_drive,virtual_disk,iteration,value,discarded,reported_value,advantage,tool,tool_version,tool_arch,os_arch");

        var byId = recording.Results.ToDictionary(r => r.TestId, StringComparer.OrdinalIgnoreCase);
        foreach (TestDescriptor test in info.Tests)
        {
            if (!byId.TryGetValue(test.Id, out TestResult? result))
            {
                continue;
            }

            Write(DriveRole.System, info.SystemDrive, result.SystemRuns, result.SystemValue);
            Write(DriveRole.Dev, info.DevDrive, result.DevRuns, result.DevValue);

            void Write(DriveRole role, DriveDescriptor drive, IReadOnlyList<double> runs, double? reported)
            {
                for (int i = 0; i < runs.Count; i++)
                {
                    sb.AppendJoin(',', new[]
                    {
                        info.StartedAt.ToString("o", CultureInfo.InvariantCulture),
                        Csv(info.Machine.Name),
                        Csv(info.Machine.Cpu),
                        Csv(info.Machine.Os),
                        info.Profile.ToString(),
                        info.Iterations.ToString(CultureInfo.InvariantCulture),
                        test.Id,
                        Csv(test.Name),
                        test.Unit,
                        test.Better.ToString(),
                        role.ToString(),
                        drive.Letter,
                        drive.FileSystem,
                        drive.IsDevDrive ? "true" : "false",
                        drive.IsVirtual ? "true" : "false",
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        runs[i].ToString("0.######", CultureInfo.InvariantCulture),
                        BenchStats.IsDiscarded(i, runs.Count) ? "true" : "false",
                        reported?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty,
                        result.Advantage?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
                        test.RequiredTool ?? string.Empty,
                        test.ToolVersion ?? string.Empty,
                        test.ToolArchitecture ?? string.Empty,
                        info.Machine.OsArchitecture,
                    });
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    private static void AddFrom(List<RecordingSummary> list, string? directory, bool bundled)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                RunRecording r = Load(path);
                if (r.Events.Count == 0)
                {
                    continue;
                }

                list.Add(new RecordingSummary(
                    path,
                    r.Info.StartedAt,
                    r.Info.Machine.Name,
                    r.Info.SystemDrive.Letter,
                    r.Info.DevDrive.Letter,
                    r.Info.Iterations,
                    r.DurationSeconds,
                    r.Outcome,
                    r.Headline,
                    bundled));
            }
            catch
            {
                // Not a recording (or a newer/corrupt one): leave it out of the picker.
            }
        }
    }

    private static string FileStem(RunRecording recording)
    {
        string machine = new string((recording.Info.Machine.Name ?? "pc").Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        string outcome = recording.Outcome == RunOutcome.Completed ? string.Empty : "_" + recording.Outcome.ToString().ToLowerInvariant();
        return $"{recording.Info.StartedAt.LocalDateTime:yyyyMMdd-HHmmss}_{machine}_{recording.Info.Iterations}x{outcome}";
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

/// <summary>
/// Plays a recorded event stream against a clock. Pure: the caller supplies the playback time, so the
/// UI timer, the tests, and any speed multiplier all share the same logic.
/// </summary>
public sealed class ReplayScheduler
{
    private readonly BenchEvent[] _events;
    private int _next;

    public ReplayScheduler(IReadOnlyList<BenchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Stable sort by time keeps same-timestamp events in recorded order.
        _events = events.Select((e, i) => (e, i)).OrderBy(x => x.e.T).ThenBy(x => x.i).Select(x => x.e).ToArray();
    }

    /// <summary>Recorded duration in seconds (timestamp of the last event).</summary>
    public double Duration => _events.Length == 0 ? 0 : _events[^1].T;

    public bool IsFinished => _next >= _events.Length;

    /// <summary>Returns every not-yet-returned event with <c>T ≤ time</c>, in order.</summary>
    public IReadOnlyList<BenchEvent> Advance(double time)
    {
        int start = _next;
        while (_next < _events.Length && _events[_next].T <= time)
        {
            _next++;
        }

        return _next == start ? Array.Empty<BenchEvent>() : new ArraySegment<BenchEvent>(_events, start, _next - start);
    }

    /// <summary>Time of the next pending event, or null when finished.</summary>
    public double? NextEventTime => IsFinished ? null : _events[_next].T;
}
