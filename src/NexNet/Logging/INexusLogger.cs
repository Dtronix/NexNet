using System;

namespace NexNet.Logging;

/// <summary>
/// Basic logging interface.
/// </summary>
public interface INexusLogger
{
    /// <summary>
    /// Category for the log event.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Gets or sets the session-specific details to be included in the log.
    /// This can be used to provide additional context about the session during which the log events occur.
    /// </summary>
    public string? SessionDetails { get; set; }

    /// <summary>
    /// Logs a message with a specified level, category, and associated exception.
    /// </summary>
    /// <param name="logLevel">The severity level of the log message.</param>
    /// <param name="category">The optional category of the log message.</param>
    /// <param name="exception">The exception related to this log event, if any.</param>
    /// <param name="message">The log message to be written.</param>
    void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message);

    /// <summary>
    /// Creates a new instance of INexusLogger with the specified category.
    /// </summary>
    /// <param name="category">The category name for the logger. This value is used to organize and filter log messages.</param>
    /// <param name="sessionDetails">The session specific details to be included in the log.</param>
    /// <returns>A new instance of INexusLogger with the specified category.</returns>
    INexusLogger CreateLogger(string? category, string? sessionDetails = null);
}
