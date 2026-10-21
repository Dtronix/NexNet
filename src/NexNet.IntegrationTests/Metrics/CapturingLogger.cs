using System.Collections.Concurrent;
using NexNet.Logging;

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// Keeps every message logged through it or its children.
/// </summary>
internal sealed class CapturingLogger : INexusLogger
{
    private readonly ConcurrentQueue<(NexusLogLevel Level, string Message)> _messages;

    public CapturingLogger() : this(new ConcurrentQueue<(NexusLogLevel, string)>(), null)
    {
    }

    private CapturingLogger(ConcurrentQueue<(NexusLogLevel, string)> messages, string? pathSegment)
    {
        _messages = messages;
        PathSegment = pathSegment;
    }

    public NexusLogBehaviors Behaviors { get; set; }
    public string FormattedPath => PathSegment ?? "";
    public string? PathSegment { get; set; }

    public IEnumerable<string> Warnings => _messages.Where(m => m.Level == NexusLogLevel.Warning).Select(m => m.Message);

    public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
        => _messages.Enqueue((logLevel, message));

    public INexusLogger CreateLogger(string? pathSegment = null) => new CapturingLogger(_messages, pathSegment);
}
