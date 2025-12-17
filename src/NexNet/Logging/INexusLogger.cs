using System;
using System.Collections.Generic;

namespace NexNet.Logging;

/// <summary>
/// Basic logging interface with hierarchical path tracking.
/// </summary>
public interface INexusLogger
{
    /// <summary>
    /// Configures the logger to behave certain ways.  Can be updated on the fly.
    /// </summary>
    public NexusLogBehaviors Behaviors { get; set; }

    /// <summary>
    /// Gets the full logging path as a formatted string for display purposes.
    /// Example: "Server→Session-123→Pipe-456→Collection-Users"
    /// </summary>
    public string FormattedPath { get; }
    
    /// <summary>
    /// Gets or sets the current path segment for this logger instance.
    /// </summary>
    public string? PathSegment { get; set; }

    /// <summary>
    /// Logs a message with a specified level, category, and associated exception.
    /// </summary>
    /// <param name="logLevel">The severity level of the log message.</param>
    /// <param name="category">The optional category of the log message.</param>
    /// <param name="exception">The exception related to this log event, if any.</param>
    /// <param name="message">The log message to be written.</param>
    void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message);

    /// <summary>
    /// Creates a new instance of INexusLogger with optional path extension.
    /// </summary>
    /// <param name="pathSegment">Optional path segment to add to the logging hierarchy (e.g., "Session-123", "Pipe-456", "Collection-Users")</param>
    /// <returns>A new instance of INexusLogger with extended path.</returns>
    INexusLogger CreateLogger(string? pathSegment = null);
    
    
}
