namespace NexNet.Interop.Remote;

/// <summary>The control connection broke the protocol (framing, message shapes, hello).</summary>
public sealed class ControlProtocolException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ControlProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ControlProtocolException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A language host crashed (it stopped answering pings or its process ended) while a request was in flight, or a
/// handle of a host that has since been restarted was used. <see cref="Hosts.RetryOnHostCrashAttribute"/> retries tests that
/// fail with it.
/// </summary>
public sealed class HostCrashedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HostCrashedException(string message, string? threadDump = null, string? stderrTail = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ThreadDump = threadDump;
        StderrTail = stderrTail;
    }

    /// <summary>The thread dump captured at the crash, if any.</summary>
    public string? ThreadDump { get; }

    /// <summary>The last lines the host wrote to stderr, if any.</summary>
    public string? StderrTail { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = base.ToString();
        if (!string.IsNullOrEmpty(StderrTail))
            text += Environment.NewLine + "--- host stderr (tail) ---" + Environment.NewLine + StderrTail;
        if (!string.IsNullOrEmpty(ThreadDump))
            text += Environment.NewLine + "--- host thread dump ---" + Environment.NewLine + ThreadDump;
        return text;
    }
}

/// <summary>A language host reported an internal error (kind <c>Internal</c> or an unknown kind).</summary>
public sealed class RemoteHostException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RemoteHostException(string kind, string message, string? remoteStack)
        : base(message)
    {
        Kind = kind;
        RemoteStack = remoteStack;
    }

    /// <summary>The error kind the host sent.</summary>
    public string Kind { get; }

    /// <summary>The host's native stack trace, if it sent one.</summary>
    public string? RemoteStack { get; }

    /// <inheritdoc />
    public override string ToString()
        => RemoteStack == null ? base.ToString() : base.ToString() + Environment.NewLine + "--- host stack ---" + Environment.NewLine + RemoteStack;
}

/// <summary>A language host left something open at the end of a test (control protocol §5.1).</summary>
public sealed class LeakDetectedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LeakDetectedException(string language, LeakReport report)
        : base($"The {language} host reported leaks at the end of the test: {report}")
    {
        Language = language;
        Report = report;
    }

    /// <summary>The language of the host.</summary>
    public string Language { get; }

    /// <summary>What leaked.</summary>
    public LeakReport Report { get; }
}
