using System;

namespace NexNet;

/// <summary>
/// Basic logging interface.
/// </summary>
public interface INexNetLogger
{

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
    /// Log event invoked on each log occurrence.
    /// </summary>
    /// <param name="logLevel">Level of the log event.</param>
    /// <param name="exception">Optional exception associated with this event.</param>
    /// <param name="message">Message for this log event.</param>
    void Log(LogLevel logLevel, Exception? exception, string message);
}

/// <summary>
/// Common logging extensions.
/// </summary>
public static class NexNetLoggerExtensions
{

    /// <summary>
    /// Log a trace event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogTrace(this INexNetLogger logger, string message)
    {
        LogTrace(logger, null, message);
    }

    /// <summary>
    /// Log a trace event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogTrace(this INexNetLogger logger, Exception? ex, string message)
    {
        logger.Log(INexNetLogger.LogLevel.Trace, ex, message);
    }

    /// <summary>
    /// Log a debug event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogDebug(this INexNetLogger logger, string message)
    {
        LogDebug(logger, null, message);
    }


    /// <summary>
    /// Log a debug event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogDebug(this INexNetLogger logger, Exception? ex, string message)
    {
        logger.Log(INexNetLogger.LogLevel.Debug, ex, message);
    }


    /// <summary>
    /// Log an info event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogInfo(this INexNetLogger logger, string message)
    {
        LogInfo(logger, null, message);
    }

    /// <summary>
    /// Log an info event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogInfo(this INexNetLogger logger, Exception? ex, string message)
    {
        logger.Log(INexNetLogger.LogLevel.Information, ex, message);
    }

    /// <summary>
    /// Log a warning event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogWarning(this INexNetLogger logger, string message)
    {
        LogWarning(logger, null, message);
    }

    /// <summary>
    /// Log a warning event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogWarning(this INexNetLogger logger, Exception? ex, string message)
    {
        logger.Log(INexNetLogger.LogLevel.Warning, ex, message);
    }

    /// <summary>
    /// Log an error event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogError(this INexNetLogger logger, string message)
    {
        LogError(logger, null, message);
    }

    /// <summary>
    /// Log an error event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogError(this INexNetLogger logger, Exception? ex, string message)
    {
        logger.Log(INexNetLogger.LogLevel.Error, ex, message);
    }

    /// <summary>
    /// Log a critical event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogCritical(this INexNetLogger logger, string message)
    {
        LogCritical(logger, null, message);
    }

    /// <summary>
    /// Log a critical event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogCritical(this INexNetLogger logger, Exception? ex, string message)
    {
        logger.Log(INexNetLogger.LogLevel.Critical, ex, message);
    }

}
