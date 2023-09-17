using System;

namespace NexNet;

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
    /// Level for the log event.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// Logs that contain the most detailed messages. These messages may contain sensitive application data.
        /// These messages are disabled by default and should never be enabled in a production environment.
        /// </summary>
        Trace = 0,

        /// <summary>
        /// Logs that are used for interactive investigation during development.  These logs should primarily contain
        /// information useful for debugging and have no long-term value.
        /// </summary>
        Debug = 1,

        /// <summary>
        /// Logs that track the general flow of the application. These logs should have long-term value.
        /// </summary>
        Information = 2,

        /// <summary>
        /// Logs that highlight an abnormal or unexpected event in the application flow, but do not otherwise cause the
        /// application execution to stop.
        /// </summary>
        Warning = 3,

        /// <summary>
        /// Logs that highlight when the current flow of execution is stopped due to a failure. These should indicate a
        /// failure in the current activity, not an application-wide failure.
        /// </summary>
        Error = 4,

        /// <summary>
        /// Logs that describe an unrecoverable application or system crash, or a catastrophic failure that requires
        /// immediate attention.
        /// </summary>
        Critical = 5,

        /// <summary>
        /// Not used for writing log messages. Specifies that a logging category should not write any messages.
        /// </summary>
        None = 6,
    }

    /// <summary>
    /// Logs a message with a specified level, category, and associated exception.
    /// </summary>
    /// <param name="logLevel">The severity level of the log message.</param>
    /// <param name="category">The optional category of the log message.</param>
    /// <param name="exception">The exception related to this log event, if any.</param>
    /// <param name="message">The log message to be written.</param>
    void Log(LogLevel logLevel, string? category, Exception? exception, string message);

    /// <summary>
    /// Creates a new instance of INexusLogger with the specified category.
    /// </summary>
    /// <param name="category">The category name for the logger. This value is used to organize and filter log messages.</param>
    /// <returns>A new instance of INexusLogger with the specified category.</returns>
    INexusLogger CreateLogger(string? category);
}

/// <summary>
/// Common logging extensions.
/// </summary>
public static class NexusLoggerExtensions
{
    /// <summary>
    /// Creates a new instance of INexusLogger with the category name derived from the type parameter.
    /// </summary>
    /// <typeparam name="T">The type from which the category name is derived.</typeparam>
    /// <returns>A new instance of INexusLogger with the category name derived from the type parameter.</returns>
    public static INexusLogger CreateLogger<T>(this INexusLogger logger)
    {
        return logger.CreateLogger(typeof(T).Name);
    }

    /// <summary>
    /// Log a trace event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogTrace(this INexusLogger logger, string message)
    {
        LogTrace(logger, null, message);
    }

    /// <summary>
    /// Log a trace event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogTrace(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(INexusLogger.LogLevel.Trace, logger.Category, ex, message);
    }

    /// <summary>
    /// Log a debug event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogDebug(this INexusLogger logger, string message)
    {
        LogDebug(logger, null, message);
    }


    /// <summary>
    /// Log a debug event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogDebug(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(INexusLogger.LogLevel.Debug, logger.Category, ex, message);
    }


    /// <summary>
    /// Log an info event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogInfo(this INexusLogger logger, string message)
    {
        LogInfo(logger, null, message);
    }

    /// <summary>
    /// Log an info event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogInfo(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(INexusLogger.LogLevel.Information, logger.Category, ex, message);
    }

    /// <summary>
    /// Log a warning event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogWarning(this INexusLogger logger, string message)
    {
        LogWarning(logger, null, message);
    }

    /// <summary>
    /// Log a warning event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogWarning(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(INexusLogger.LogLevel.Warning, logger.Category, ex, message);
    }

    /// <summary>
    /// Log an error event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogError(this INexusLogger logger, string message)
    {
        LogError(logger, null, message);
    }

    /// <summary>
    /// Log an error event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogError(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(INexusLogger.LogLevel.Error, logger.Category, ex, message);
    }

    /// <summary>
    /// Log a critical event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogCritical(this INexusLogger logger, string message)
    {
        LogCritical(logger, null, message);
    }

    /// <summary>
    /// Log a critical event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogCritical(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(INexusLogger.LogLevel.Critical, logger.Category, ex, message);
    }

}
