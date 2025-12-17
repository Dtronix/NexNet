using System;
using System.Diagnostics;
using System.Text;

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
    [Conditional("DEBUG")]
    public static void LogTrace(this INexusLogger logger, string message)
    {
        logger.LogTrace(null, message);
    }

    /// <summary>
    /// Log a trace event containing data..
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    /// <param name="data">Data to log.</param>
    [Conditional("DEBUG")]
    internal static void LogTraceArray(this INexusLogger logger, string message, ReadOnlyMemory<byte> data)
    {
        var sb = new StringBuilder(message);
        sb.Append("[");
        foreach (var byteData in data.Span)
        {
            sb.Append(byteData).Append(',');
        }
        if(data.Length > 0)
            sb.Remove(sb.Length - 1, 1);
        sb.Append("]");
        logger.LogTrace(null, sb.ToString());
    }

    /// <summary>
    /// Log a trace event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="ex">Optional exception which is associated with this log event.</param>
    /// <param name="message">Log message.</param>
    [Conditional("DEBUG")]
    public static void LogTrace(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(NexusLogLevel.Trace, null, ex, message);
    }

    /// <summary>
    /// Log a debug event.
    /// </summary>
    /// <param name="logger">Logger for this method.</param>
    /// <param name="message">Log message.</param>
    [Conditional("DEBUG")]
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
    [Conditional("DEBUG")]
    public static void LogDebug(this INexusLogger logger, Exception? ex, string message)
    {
        logger.Log(NexusLogLevel.Debug, null, ex, message);
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
        logger.Log(NexusLogLevel.Information, null, ex, message);
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
        logger.Log(NexusLogLevel.Warning, null, ex, message);
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
        logger.Log(NexusLogLevel.Error, null, ex, message);
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
        logger.Log(NexusLogLevel.Critical, null, ex, message);
    }

}
