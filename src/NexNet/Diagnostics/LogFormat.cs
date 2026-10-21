using System.Globalization;

namespace NexNet.Diagnostics;

/// <summary>
/// Formats byte counts and durations for log messages.
/// </summary>
internal static class LogFormat
{
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB",
    };

    public static string Seconds(long milliseconds)
        => (milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
