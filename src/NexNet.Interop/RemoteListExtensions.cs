namespace NexNet.Interop;

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
}
