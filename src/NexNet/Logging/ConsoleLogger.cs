using System;

namespace NexNet.Logging;

/// <summary>
/// Represents a logger that outputs log messages to the console.
/// </summary>
public class ConsoleLogger : CoreLogger<ConsoleLogger>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleLogger"/> class.
    /// </summary>
    /// <param name="parentLogger">The parent logger for hierarchical logging.</param>
    /// <param name="pathSegment">The path segment to append to the logger's path.</param>
    public ConsoleLogger(ConsoleLogger? parentLogger = null, string? pathSegment = null)
        : base(parentLogger, pathSegment)
    {

    }


    /// <inheritdoc/>
    public override void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        var log = GetFormattedLogString(logLevel, category, exception, message);
        
        if(log != null)
            Console.WriteLine(log);
    }

    /// <inheritdoc/>
    public override INexusLogger CreateLogger(string? pathSegment = null)
    {
        return new ConsoleLogger(this, pathSegment);
    }
}
