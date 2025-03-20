using System;
using Microsoft.Extensions.Logging;
using NexNet.Logging;

namespace NexNet.Asp;

/// <summary>
/// Bridge for ILogger and INexusLogger
/// </summary>
public class LoggerNexusBridge : INexusLogger
{
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a bridge for ILogger and INexusLogger
    /// </summary>
    /// <param name="logger"></param>
    public LoggerNexusBridge(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string? Category { get; set; }

    /// <inheritdoc />
    public string? SessionDetails { get; set; }

    /// <inheritdoc />
    public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        _logger.Log((LogLevel)logLevel, exception, message);
    }

    /// <inheritdoc />
    public INexusLogger CreateLogger(string? category, string? sessionDetails = null)
    {
        return this;
    }
}
