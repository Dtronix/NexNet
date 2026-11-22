using NexNet.Logging;

namespace NexNet.Interop.InProc;

/// <summary>Passes the lines at or above a level to another logger (<see cref="ClientSpec.LogLevel"/> of the C# client).</summary>
internal sealed class LevelFilterLogger(INexusLogger inner, NexusLogLevel minimum) : INexusLogger
{
    /// <inheritdoc />
    public NexusLogBehaviors Behaviors
    {
        get => inner.Behaviors;
        set => inner.Behaviors = value;
    }

    /// <inheritdoc />
    public string FormattedPath => inner.FormattedPath;

    /// <inheritdoc />
    public string? PathSegment
    {
        get => inner.PathSegment;
        set => inner.PathSegment = value;
    }

    /// <inheritdoc />
    public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
    {
        if (logLevel >= minimum && logLevel != NexusLogLevel.None)
            inner.Log(logLevel, category, exception, message);
    }

    /// <inheritdoc />
    public INexusLogger CreateLogger(string? pathSegment = null) => new LevelFilterLogger(inner.CreateLogger(pathSegment), minimum);
}
