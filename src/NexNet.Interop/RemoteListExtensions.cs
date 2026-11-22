namespace NexNet.Interop;

/// <summary>
/// The result of a list operation together with the number of items the client's list held when the operation completed
/// (<see cref="RemoteListExtensions.RemoveAtWithCountAsync{T}"/>).
/// </summary>
/// <param name="Ok">The operation's result: true when the server applied it, false for a Noop.</param>
/// <param name="Count">The list's count, read in the client the moment the operation completed.</param>
public readonly record struct RemoteListOpResult(bool Ok, int Count);

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
