using System.Collections.Concurrent;

namespace NexNet.Interop.Remote;

/// <summary>
/// The forwarded calls (control protocol §7.3) a driver is running, by call ID. A call ends on its own task while
/// <c>script.forwardCancel</c> and the driver's dispose cancel from other threads, so a cancel can find a call that
/// has already finished and released its source: that cancel does nothing.
/// </summary>
internal sealed class ForwardedCalls
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _calls = new(StringComparer.Ordinal);

    /// <summary>The number of calls still running.</summary>
    public int Count => _calls.Count;

    /// <summary>Starts the call <paramref name="callId"/> and returns the token its handler gets.</summary>
    public CancellationToken Start(string callId)
    {
        var source = new CancellationTokenSource();
        _calls[callId] = source;
        return source.Token;
    }

    /// <summary>Ends the call <paramref name="callId"/> and releases its token source.</summary>
    public void Complete(string callId)
    {
        if (_calls.TryRemove(callId, out var source))
            source.Dispose();
    }

    /// <summary>Cancels the call <paramref name="callId"/>; false when it is unknown or has finished already.</summary>
    public bool Cancel(string callId)
        => _calls.TryGetValue(callId, out var source) && TryCancel(source);

    /// <summary>Cancels every running call.</summary>
    public void CancelAll()
    {
        foreach (var source in _calls.Values)
            TryCancel(source);
    }

    /// <summary>Cancels <paramref name="source"/>; false when its call finished and disposed it in the meantime.</summary>
    internal static bool TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The call completed between the lookup and the cancel: nothing left to cancel.
            return false;
        }
    }
}
