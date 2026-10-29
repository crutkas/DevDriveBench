using System.Diagnostics;
using DevDriveBench.Engine.Model;

namespace DevDriveBench.Engine.Platform;

/// <summary>Source of periodic machine-activity readings during a run.</summary>
public interface IActivitySource : IDisposable
{
    /// <summary>Reads current activity, or null when counters are unavailable.</summary>
    MachineActivityEvent? Sample();
}

/// <summary>
/// Performance-counter activity: <c>LogicalDisk(X:)\Disk Bytes/sec</c> for each drive and
/// <c>Processor(_Total)\% Processor Time</c>. Missing counters degrade to zero rather than failing a run.
/// </summary>
public sealed class PerfCounterActivitySource : IActivitySource
{
    private readonly PerformanceCounter? _system;
    private readonly PerformanceCounter? _dev;
    private readonly PerformanceCounter? _cpu;

    public PerfCounterActivitySource(DriveDescriptor systemDrive, DriveDescriptor devDrive)
    {
        _system = TryCreate("LogicalDisk", "Disk Bytes/sec", systemDrive.Letter);
        _dev = TryCreate("LogicalDisk", "Disk Bytes/sec", devDrive.Letter);
        _cpu = TryCreate("Processor", "% Processor Time", "_Total");
    }

    public MachineActivityEvent? Sample()
    {
        if (_system is null && _dev is null && _cpu is null)
        {
            return null;
        }

        return new MachineActivityEvent(
            Math.Round(Read(_system) / 1_000_000d, 2),
            Math.Round(Read(_dev) / 1_000_000d, 2),
            Math.Round(Math.Clamp(Read(_cpu), 0d, 100d), 1));
    }

    public void Dispose()
    {
        _system?.Dispose();
        _dev?.Dispose();
        _cpu?.Dispose();
    }

    /// <summary>
    /// Pays the one-time (multi-second) performance-counter initialization cost up front, e.g. at app
    /// start on a background thread, so the first run's activity trace starts immediately.
    /// </summary>
    public static void Warm()
    {
        using PerformanceCounter? pc = TryCreate("LogicalDisk", "Disk Bytes/sec", "_Total");
        using PerformanceCounter? cpu = TryCreate("Processor", "% Processor Time", "_Total");
    }

    private static PerformanceCounter? TryCreate(string category, string counter, string instance)
    {
        try
        {
            var pc = new PerformanceCounter(category, counter, instance, readOnly: true);
            pc.NextValue(); // prime: the first read of a rate counter is always 0
            return pc;
        }
        catch
        {
            return null;
        }
    }

    private static double Read(PerformanceCounter? counter)
    {
        try
        {
            return counter is null ? 0d : counter.NextValue();
        }
        catch
        {
            return 0d;
        }
    }
}
