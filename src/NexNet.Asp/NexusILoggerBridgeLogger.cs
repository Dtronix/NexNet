using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NexNet.Logging;

namespace NexNet.Asp;

/// <summary>
/// Bridge for ILogger and INexusLogger
/// </summary>
public class NexusILoggerBridgeLogger : INexusLogger
{
    private readonly ILogger? _logger;
    private readonly Stopwatch _sw;

    private readonly NexusILoggerBridgeLogger _baseLogger;
    private NexusLogBehaviors _behaviors = NexusLogBehaviors.Default;

    /// <inheritdoc />
    public NexusLogBehaviors Behaviors
    {
        get => _baseLogger._behaviors;
        set => _baseLogger._behaviors = value;
    }

    /// <inheritdoc />
    public string? Category { get; }

    /// <inheritdoc />
    public string? SessionDetails { get; set; }

    /// <summary>
    /// Creates a logger wrapper from a ILogger interface.
    /// </summary>
    /// <param name="logger"></param>
    public NexusILoggerBridgeLogger(ILogger logger)
    {
        _baseLogger = this;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sw = Stopwatch.StartNew();
    }

    private NexusILoggerBridgeLogger(NexusILoggerBridgeLogger baseLogger, string? category, string? sessionDetails)
    {
        _baseLogger = baseLogger;
        _logger = null;
        Category = category;
        SessionDetails = sessionDetails;
        _sw = Stopwatch.StartNew();
    }


    /// <inheritdoc />
    public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        var time = _sw.ElapsedTicks / (double)Stopwatch.Frequency;

        _baseLogger._logger!.Log((LogLevel)logLevel, exception, $"[{time:0.000000}][{category}:{SessionDetails}] {message} {exception}", (string?)null);
    }

    /// <inheritdoc />
    public INexusLogger CreateLogger(string? category, string? sessionDetails = null)
    {
        return new NexusILoggerBridgeLogger(_baseLogger, category, sessionDetails ?? SessionDetails);
    }
}
