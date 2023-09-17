using System;

namespace NexNet.Logging;

/// <summary>
/// Common logging extensions.
/// </summary>
public static class NexusLoggerExtensions
{
    /// <summary>
    /// Log a trace event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogTrace(this INexusLogger logger, string message)
    {
        logger.LogTrace(null, message);
    }

    /// <summary>
    /// Log a trace event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogTrace(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(NexusLogLevel.Trace, logger.Category, ex, message);
    }

    /// <summary>
    /// Log a debug event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogDebug(this INexusLogger logger, string message)
    {
        logger.LogDebug(null, message);
    }


    /// <summary>
    /// Log a debug event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogDebug(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(NexusLogLevel.Debug, logger.Category, ex, message);
    }


    /// <summary>
    /// Log an info event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogInfo(this INexusLogger logger, string message)
    {
        logger.LogInfo(null, message);
    }

    /// <summary>
    /// Log an info event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogInfo(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(NexusLogLevel.Information, logger.Category, ex, message);
    }

    /// <summary>
    /// Log a warning event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogWarning(this INexusLogger logger, string message)
    {
        logger.LogWarning(null, message);
    }

    /// <summary>
    /// Log a warning event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogWarning(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(NexusLogLevel.Warning, logger.Category, ex, message);
    }

    /// <summary>
    /// Log an error event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogError(this INexusLogger logger, string message)
    {
        logger.LogError(null, message);
    }

    /// <summary>
    /// Log an error event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogError(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(NexusLogLevel.Error, logger.Category, ex, message);
    }

    /// <summary>
    /// Log a critical event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    public static void LogCritical(this INexusLogger logger, string message)
    {
        logger.LogCritical(null, message);
    }

    /// <summary>
    /// Log a critical event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    public static void LogCritical(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(NexusLogLevel.Critical, logger.Category, ex, message);
    }

}
