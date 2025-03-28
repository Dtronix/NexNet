using System;
using Microsoft.Extensions.Logging;
using NexNet.Logging;

namespace NexNet.Asp;

/// <summary>
/// Bridge for ILogger and INexusLogger
/// </summary>
public class NexusILoggerBridgeLogger : CoreLogger
{
    private readonly ILogger _logger;

    public NexusILoggerBridgeLogger(ILogger logger)
    {
        _logger = logger;
    }

    private NexusILoggerBridgeLogger(CoreLogger baseLogger, string? category, string? prefix, string? sessionDetails)
        : base(baseLogger)
    {
        Prefix = prefix;
        Category = category;
        SessionDetails = sessionDetails;
    }


    public override void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        if (!this.BaseLogger.LogEnabled)
            return;

        if (logLevel < this.BaseLogger.MinLogLevel)
            return;

        _logger.Log((LogLevel)logLevel, exception, Prefix != null
            ? $"{Prefix} [{category}:{SessionDetails}] {message} {exception}"
            : $"[{category}:{SessionDetails}] {message} {exception}", (string?)null);
    }

    public override INexusLogger CreateLogger(string? category, string? sessionDetails = null)
    {
        return new NexusILoggerBridgeLogger(BaseLogger, category, Prefix, sessionDetails ?? SessionDetails);
    }

    /// <inheritdoc/>
    public override CoreLogger CreatePrefixedLogger(string? category, string prefix, string? sessionDetails = null)
    {
        return new NexusILoggerBridgeLogger(BaseLogger, category, prefix, sessionDetails ?? SessionDetails);
    }
}
