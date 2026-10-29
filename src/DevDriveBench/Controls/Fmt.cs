using System.Globalization;

namespace DevDriveBench.Controls;

/// <summary>Number formatting with precision matched to magnitude (no false precision on stage).</summary>
public static class Fmt
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>A value in its unit: "6.61 s", "48.2 s", "1,784 MB/s", "7,653 IOPS".</summary>
    public static string Value(double value, string unit) => unit switch
    {
        "s" => Seconds(value),
        "IOPS" => value.ToString("N0", Culture) + " IOPS",
        _ => value.ToString("N0", Culture) + " " + unit,
    };

    public static string Seconds(double seconds) => seconds switch
    {
        < 10 => seconds.ToString("0.00", Culture) + " s",
        < 100 => seconds.ToString("0.0", Culture) + " s",
        _ => seconds.ToString("0", Culture) + " s",
    };

    /// <summary>Advantage: "4.24×".</summary>
    public static string Ratio(double ratio) => ratio.ToString("0.00", Culture) + "×";

    /// <summary>Headline advantage: "4.2×".</summary>
    public static string Headline(double ratio) => ratio.ToString(ratio >= 10 ? "0" : "0.0", Culture) + "×";

    /// <summary>Clock: "0:07.4" under a minute, "2:13" beyond.</summary>
    public static string Clock(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalMinutes < 1
            ? $"0:{span.Seconds:00}.{span.Milliseconds / 100}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    /// <summary>Throughput for the activity trace: "412 MB/s" or "1.2 GB/s".</summary>
    public static string Throughput(double mbps) => mbps >= 1000
        ? (mbps / 1000).ToString("0.0", Culture) + " GB/s"
        : mbps.ToString("0", Culture) + " MB/s";
}
