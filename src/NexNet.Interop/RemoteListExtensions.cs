namespace NexNet.Interop;

/// <summary>
/// The result of a list operation together with the number of items the client's list held when the operation completed
/// (<see cref="RemoteListExtensions.RemoveAtWithCountAsync{T}"/>).
/// </summary>
/// <param name="Ok">The operation's result: true when the server applied it, false for a Noop.</param>
/// <param name="Count">The list's count, read in the client the moment the operation completed.</param>
public readonly record struct RemoteListOpResult(bool Ok, int Count);

/// <summary>
/// A slow consumer for a client's list (control protocol §6.10 <c>list.throttle</c>): the list's process test hook (C#
/// <c>NexusBroadcastClient.ProcessTestHook</c>, Java <c>NexusListClient.setProcessTestHook</c>, JS
/// <c>NexusList.setProcessTestHook</c>) delays or holds received list messages before the client processes them. While it
/// does, the client reads nothing more from the collection pipe and grants the server no credit.
/// </summary>
/// <remarks>
/// Messages are counted from the throttle's installation, the first one is 1. The first message is held when
/// <see cref="Pause"/> is set, else delayed by <see cref="FirstDelayMs"/>; every later message whose number is a multiple
/// of <see cref="Every"/> is delayed by <see cref="DelayMs"/>.
/// </remarks>
public sealed record RemoteListThrottle
{
    /// <summary>Delays every <see cref="Every"/>th message by <see cref="DelayMs"/> (at least 1; default 1, every message).</summary>
    public int Every { get; init; } = 1;

    /// <summary>The delay of every <see cref="Every"/>th message, in milliseconds; 0 for none.</summary>
    public int DelayMs { get; init; }

    /// <summary>The delay of the first message, in milliseconds; 0 for none.</summary>
    public int FirstDelayMs { get; init; }

    /// <summary>
    /// Holds the first message until the throttle is replaced or removed (<see cref="RemoteListExtensions.ThrottleAsync{T}"/>)
    /// or 60 seconds pass; a language host also releases it when it disposes the client.
    /// </summary>
    public bool Pause { get; init; }

    /// <summary>Whether the throttle does anything; one that does not removes the hook.</summary>
    internal bool IsActive => DelayMs > 0 || FirstDelayMs > 0 || Pause;
}

/// <summary>What a <see cref="RemoteListThrottle"/> has seen (control protocol §6.10 <c>list.throttleState</c>).</summary>
/// <param name="Seen">The received messages the throttle was called for since its installation.</param>
/// <param name="Holding">Whether it holds a message now (<see cref="RemoteListThrottle.Pause"/>).</param>
public readonly record struct RemoteListThrottleState(int Seen, bool Holding);

/// <summary>Harness-only operations on an <see cref="IRemoteList{T}"/> of a client under test.</summary>
public static class RemoteListExtensions
{
    /// <summary>The message of the exception a throwing watch raises.</summary>
    public const string ThrowingWatchMessage = "A scripted list change handler threw (list.watch throws).";

    /// <summary>
    /// Starts a watch whose change handler, in the client under test, throws a native exception after recording each
    /// change (control protocol §6.10 <c>list.watch</c> with <c>throws</c>): C# <see cref="InvalidOperationException"/>,
    /// Java <c>IllegalStateException</c>, JS <c>Error</c>. The client must keep running and keep raising the change
    /// to its other handlers.
    /// </summary>
    /// <exception cref="NotSupportedException">The list is not a list of a harness driver.</exception>
    public static ValueTask<IRemoteListWatch<T>> WatchThrowingAsync<T>(this IRemoteList<T> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list switch
        {
            Remote.RemoteList<T> remote => remote.WatchAsync(throws: true),
            InProc.InProcRemoteList<T> inProc => inProc.WatchAsync(throws: true),
            _ => throw new NotSupportedException($"{list.GetType()} cannot install a throwing change handler."),
        };
    }

    /// <summary>
    /// Installs, replaces or (with null or an inactive throttle) removes a slow consumer on the client's list (control
    /// protocol §6.10 <c>list.throttle</c>). Replacing or removing a throttle releases the message it holds. Works whether
    /// or not the collection is enabled.
    /// </summary>
    /// <exception cref="NotSupportedException">The list is not a list of a harness driver.</exception>
    public static ValueTask ThrottleAsync<T>(this IRemoteList<T> list, RemoteListThrottle? throttle)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list switch
        {
            Remote.RemoteList<T> remote => remote.ThrottleAsync(throttle),
            InProc.InProcRemoteList<T> inProc => inProc.ThrottleAsync(throttle),
            _ => throw new NotSupportedException($"{list.GetType()} cannot throttle its list."),
        };
    }

    /// <summary>
    /// What the client list's throttle has seen, and whether it holds a message (control protocol §6.10
    /// <c>list.throttleState</c>); zero and false without a throttle.
    /// </summary>
    /// <exception cref="NotSupportedException">The list is not a list of a harness driver.</exception>
    public static ValueTask<RemoteListThrottleState> GetThrottleStateAsync<T>(this IRemoteList<T> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list switch
        {
            Remote.RemoteList<T> remote => remote.GetThrottleStateAsync(),
            InProc.InProcRemoteList<T> inProc => new ValueTask<RemoteListThrottleState>(inProc.GetThrottleState()),
            _ => throw new NotSupportedException($"{list.GetType()} cannot throttle its list."),
        };
    }

    /// <summary>
    /// Removes the item at <paramref name="index"/> and returns the result together with the list's count, which the
    /// client reads the moment the remove completes, before it applies anything else (control protocol §6.10
    /// <c>list.op</c> with <c>count</c>). A remove that completed with false (a Noop) must already see the change that
    /// made it obsolete; a count read by a later round trip cannot tell.
    /// </summary>
    /// <exception cref="NotSupportedException">The list is not a list of a harness driver.</exception>
    public static ValueTask<RemoteListOpResult> RemoveAtWithCountAsync<T>(this IRemoteList<T> list, int index)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list switch
        {
            Remote.RemoteList<T> remote => remote.RemoveAtWithCountAsync(index),
            InProc.InProcRemoteList<T> inProc => inProc.RemoveAtWithCountAsync(index),
            _ => throw new NotSupportedException($"{list.GetType()} cannot report its count with an operation's result."),
        };
    }
}
