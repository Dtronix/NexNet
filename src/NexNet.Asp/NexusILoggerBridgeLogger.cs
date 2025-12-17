using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NexNet.Logging;

namespace NexNet.Asp;

/// <summary>
/// Bridge for ILogger and INexusLogger
/// </summary>
public class NexusILoggerBridgeLogger : CoreLogger<NexusILoggerBridgeLogger>
{
    private readonly ILogger _logger;


    /// <summary>
    /// Initializes a new instance of the NexusILoggerBridgeLogger with the specified ILogger and optional base logger.
    /// </summary>
    /// <param name="logger">The Microsoft.Extensions.Logging.ILogger to bridge to.</param>
    /// <param name="parentLogger">The optional base CoreLogger for hierarchical logging.</param>
    /// <param name="pathSegment">The optional path segment for this logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when logger is null.</exception>
    public NexusILoggerBridgeLogger(ILogger logger, NexusILoggerBridgeLogger? parentLogger = null, string? pathSegment = null)
        : base(parentLogger, pathSegment)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        var log = GetFormattedLogString(logLevel, category, exception, message);
        
        if(log != null)
            _logger.Log((LogLevel)logLevel, exception, log, (string?)null);
        
    }

    /// <inheritdoc />
    public override INexusLogger CreateLogger(string? pathSegment = null)
    {
        return new NexusILoggerBridgeLogger(_logger, this, pathSegment);
    }
}
