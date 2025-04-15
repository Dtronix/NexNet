using System;
using System.Diagnostics;

namespace NexNet.Logging;

/// <summary>
/// The CoreLogger is an abstract class that provides the base functionality for logging in the application. 
/// It implements the INexusLogger interface and provides the basic structure for logging, including managing the minimum log level, 
/// categorizing log events, and enabling or disabling logging. 
/// It also provides the ability to create new instances of the logger with specific categories and session details.
/// </summary>
public abstract class CoreLogger : INexusLogger
{
    private NexusLogBehaviors _behaviors;

    /// <summary>
    /// Gets or sets the prefix for the logger. This property is used to prepend a string to the log message, 
    /// providing a way to distinguish or categorize log messages. This can be null.
    /// </summary>
    protected string? Prefix;

    /// <summary>
    /// Represents a Stopwatch instance used for tracking the elapsed time since the logger's creation.
    /// This elapsed time is used in the log output to indicate when each log event occurred relative to the start of the logger.
    /// </summary>
    protected readonly Stopwatch Sw;

    /// <summary>
    /// Represents the base logger used in the CoreLogger class. 
    /// It is used to manage the minimum log level and to provide a reference for the logger's own instance.
    /// </summary>
    protected readonly CoreLogger BaseLogger;


    /// <summary>
    /// Represents the minimum log level for the base logger. 
    /// Log events with a level lower than this will not be processed by the base logger.
    /// </summary>
    private NexusLogLevel _thisMinLogLevel = NexusLogLevel.Trace;

    /// <inheritdoc />
    public NexusLogBehaviors Behaviors
    {
        get => BaseLogger._behaviors;
        set => BaseLogger._behaviors = value;
    }

    /// <summary>
    /// Gets or sets the category for the log event. This property is used to group related log events, 
    /// providing a way to filter or search for specific types of log events.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Gets or sets the session-specific details for the logger. 
    /// These details provide additional context about the session during which the log events occur.
    /// </summary>
    public string? SessionDetails { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether logging is enabled or disabled.
    /// The default value is true.
    /// </summary>
    public bool LogEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the minimum log level for the logger. 
    /// Log events with a level lower than this will not be processed by the logger.
    /// </summary>
    /// <value>
    /// The minimum log level for the logger. The default value is <see cref="NexusLogLevel.Trace"/>.
    /// </value>
    public NexusLogLevel MinLogLevel
    {
        get => BaseLogger._thisMinLogLevel;
        set => BaseLogger._thisMinLogLevel = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CoreLogger"/> class.
    /// </summary>
    protected CoreLogger()
    {
        BaseLogger = this;
        Sw = Stopwatch.StartNew();
        SessionDetails = "";
    }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="CoreLogger"/> class.
    /// </summary>
    protected CoreLogger(CoreLogger baseLogger)
    {
        BaseLogger = baseLogger;
        SessionDetails = "";
        Sw = Stopwatch.StartNew();
        Behaviors = baseLogger.Behaviors;
    }

    /// <inheritdoc />
    public abstract void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message);

    /// <inheritdoc />
    public abstract INexusLogger CreateLogger(string? category, string? sessionDetails = null);

    /// <summary>
    /// Creates a new instance of the logger with the specified category, prefix, and session details.
    /// </summary>
    /// <param name="category">The category for the logger. This can be null.</param>
    /// <param name="prefix">The prefix for the logger.</param>
    /// <param name="sessionDetails">The session-specific details to be included in the log. This can be null.</param>
    /// <returns>A new instance of the logger class.</returns>
    public abstract CoreLogger CreatePrefixedLogger(string? category, string prefix, string? sessionDetails = null);
}
