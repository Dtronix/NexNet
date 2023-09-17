using System;
using System.Diagnostics;
using System.IO;

namespace NexNet.Logging;

/// <summary>
/// Represents a logger that outputs log messages to the console.
/// </summary>
public class ConsoleLogger : CoreLogger
{
    private readonly ConsoleLogger _baseLogger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleLogger"/> class.
    /// </summary>
    public ConsoleLogger()
        : base()
    {
        _baseLogger = this;
    }

    private ConsoleLogger(ConsoleLogger baseLogger, string? category, string? prefix = null, string? sessionDetails = null)
    {
        _baseLogger = baseLogger;
        Prefix = prefix;
        SessionDetails = sessionDetails ?? "";
        Category = category;
    }


    /// <inheritdoc/>
    public override void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        if (!_baseLogger.LogEnabled)
            return;

        if (logLevel < MinLogLevel)
            return;
        var time = _baseLogger.Sw.ElapsedTicks / (double)Stopwatch.Frequency;

        Console.WriteLine(Prefix != null
            ? $"[{time:0.000000}]{Prefix} [{category}:{SessionDetails}] {message} {exception}"
            : $"[{time:0.000000}] [{category}:{SessionDetails}] {message} {exception}");
    }

    /// <inheritdoc/>
    public override INexusLogger CreateLogger(string? category, string? sessionDetails = null)
    {
        return new ConsoleLogger(_baseLogger, category, Prefix, sessionDetails ?? SessionDetails);
    }

    /// <inheritdoc/>
    public override CoreLogger CreatePrefixedLogger(string? category, string prefix, string? sessionDetails = null)
    {
        return new ConsoleLogger(_baseLogger, category, prefix, sessionDetails ?? SessionDetails);
    }
}
