using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NexNet.Interop;

/// <summary>The length and SHA-256 of the bytes a pipe read.</summary>
/// <param name="Length">Number of bytes.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the bytes.</param>
public readonly record struct PipeDigest(long Length, string Sha256);

/// <summary>
/// A duplex pipe owned by the client under test, driven from the test. Replaces <c>INexusDuplexPipe</c>, which cannot
/// be remoted: bulk data travels as seeded patterns and digests, never as large payloads.
/// </summary>
public interface IRemotePipe : IAsyncDisposable
{
    /// <summary>Completes when both sides have the pipe.</summary>
    ValueTask WhenReadyAsync();

    /// <summary>Completes when the pipe is complete in both directions.</summary>
    ValueTask WhenCompletedAsync();

    /// <summary>Writes bytes to the other side.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Writes bytes to the other side and reports whether the pipe is still open: false when its writing side is
    /// complete (the other side completed its reading side or the whole pipe, or the session ended), so the bytes were
    /// not all delivered. Unlike <see cref="WriteAsync"/>, a completed pipe is a result, not an error (C#
    /// <c>FlushResult.IsCompleted</c>).
    /// </summary>
    ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data);

    /// <summary>Writes <paramref name="length"/> bytes of the deterministic pattern of <paramref name="seed"/>.</summary>
    ValueTask WritePatternAsync(int seed, long length);

    /// <summary>Reads at least <paramref name="minimum"/> and at most <paramref name="maximum"/> bytes; fewer at completion.</summary>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(int minimum, int maximum);

    /// <summary>Reads until the other side completes and returns the digest of everything read.</summary>
    ValueTask<PipeDigest> DrainAsync();

    /// <summary>Completes the writing side.</summary>
    ValueTask CompleteAsync();

    /// <summary>
    /// Completes the reading side only (C# <c>Input.CompleteAsync()</c>): the other side's writes report the pipe
    /// complete, and this side can still write. Needs <c>ClientCapabilities.PipeInputCompletion</c>.
    /// </summary>
    ValueTask CompleteInputAsync();

    /// <summary>Uses the pipe as the writer of a channel of <typeparamref name="T"/> (codec from <see cref="TypedCodecs"/>).</summary>
    ValueTask<IRemoteChannelWriter<T>> GetChannelWriterAsync<T>();

    /// <summary>Uses the pipe as the reader of a channel of <typeparamref name="T"/> (codec from <see cref="TypedCodecs"/>).</summary>
    ValueTask<IRemoteChannelReader<T>> GetChannelReaderAsync<T>();
}

/// <summary>The writer of a remote channel.</summary>
public interface IRemoteChannelWriter<T>
{
    /// <summary>Writes one item; false when the channel is complete.</summary>
    ValueTask<bool> WriteAsync(T item);

    /// <summary>Writes several items; false when the channel is complete.</summary>
    ValueTask<bool> WriteManyAsync(IEnumerable<T> items);

    /// <summary>Completes the channel.</summary>
    ValueTask CompleteAsync();
}

/// <summary>The reader of a remote channel.</summary>
public interface IRemoteChannelReader<T>
{
    /// <summary>Reads up to <paramref name="maximum"/> items; empty when the channel is complete.</summary>
    ValueTask<IReadOnlyList<T>> ReadBatchAsync(int maximum);

    /// <summary>
    /// Reads up to <paramref name="maximum"/> items, waiting for at least one until <paramref name="cancel"/>; empty
    /// when the channel is complete. A canceled read throws <see cref="TaskCanceledException"/> and consumes no item, so
    /// the next read gets the items that arrive after it. Needs <c>ClientCapabilities.ChannelReadCancellation</c>.
    /// </summary>
    ValueTask<IReadOnlyList<T>> ReadBatchAsync(int maximum, RemoteCancel cancel);

    /// <summary>Reads until the channel completes.</summary>
    ValueTask<IReadOnlyList<T>> ReadAllAsync();
}

/// <summary>
/// The client-side view of a synchronized collection on a client under test. Reads are asynchronous snapshots, never
/// synchronous state.
/// </summary>
public interface IRemoteList<T>
{
    /// <summary>The collection.</summary>
    RemoteCollection Collection { get; }

    /// <summary>Connects the collection; false when it could not be enabled.</summary>
    ValueTask<bool> EnableAsync(RemoteCancel? cancel = null);

    /// <summary>Disconnects the collection.</summary>
    ValueTask DisableAsync();

    /// <summary>Adds an item.</summary>
    ValueTask<bool> AddAsync(T item);

    /// <summary>Inserts an item.</summary>
    ValueTask<bool> InsertAsync(int index, T item);

    /// <summary>Removes an item.</summary>
    ValueTask<bool> RemoveAsync(T item);

    /// <summary>Removes the item at an index.</summary>
    ValueTask<bool> RemoveAtAsync(int index);

    /// <summary>Replaces the item at an index.</summary>
    ValueTask<bool> ReplaceAsync(int index, T item);

    /// <summary>Moves an item.</summary>
    ValueTask<bool> MoveAsync(int fromIndex, int toIndex);

    /// <summary>Removes every item.</summary>
    ValueTask<bool> ClearAsync();

    /// <summary>The items the client currently has.</summary>
    ValueTask<IReadOnlyList<T>> SnapshotAsync();

    /// <summary>Waits until a snapshot satisfies <paramref name="predicate"/> and returns it.</summary>
    ValueTask<IReadOnlyList<T>> WaitForAsync(Func<IReadOnlyList<T>, bool> predicate, TimeSpan timeout);

    /// <summary>
    /// Starts recording the change events the client raises for this collection (control protocol §6.10
    /// <c>list.watch</c>). Changes raised after this completes are recorded; dispose the watch to stop.
    /// </summary>
    ValueTask<IRemoteListWatch<T>> WatchAsync();
}
