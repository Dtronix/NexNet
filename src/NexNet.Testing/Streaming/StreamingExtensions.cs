using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Pipes;

namespace NexNet.Testing.Streaming;

/// <summary>
/// Convenience extensions over <see cref="IRentedNexusDuplexPipe"/> for the most common
/// test-side patterns: pushing a buffer of bytes, draining a buffer of bytes, publishing a
/// fixed batch of typed items, and collecting all typed items until the channel closes.
/// </summary>
/// <remarks>
/// The plan referred to these as <c>PipeUpload</c> / <c>PipeDownload</c> / <c>ChannelPublish</c>
/// / <c>ChannelCollect</c>. They are thin wrappers — each replaces 3-4 boilerplate lines (await
/// ReadyTask, do work on the writer/reader, complete) with a single call so tests stay focused
/// on the assertion rather than the plumbing.
/// </remarks>
public static class StreamingExtensions
{
    /// <summary>
    /// Awaits <see cref="INexusDuplexPipe.ReadyTask"/>, writes every byte in <paramref name="bytes"/>
    /// to the pipe's output, then completes the writer. Equivalent to:
    /// <c>await pipe.ReadyTask; await pipe.Output.WriteAsync(bytes); await pipe.CompleteAsync();</c>.
    /// </summary>
    public static async ValueTask PipeUploadAsync(
        this IRentedNexusDuplexPipe pipe,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (pipe is null) throw new ArgumentNullException(nameof(pipe));
        await pipe.ReadyTask.ConfigureAwait(false);
        if (!bytes.IsEmpty)
            await pipe.Output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits <see cref="INexusDuplexPipe.ReadyTask"/>, reads all bytes from the pipe until the
    /// writer completes, then returns the concatenated buffer. Useful for asserting on the full
    /// payload of a download method.
    /// </summary>
    public static async ValueTask<byte[]> PipeDownloadAsync(
        this IRentedNexusDuplexPipe pipe,
        CancellationToken cancellationToken = default)
    {
        if (pipe is null) throw new ArgumentNullException(nameof(pipe));
        await pipe.ReadyTask.ConfigureAwait(false);

        using var ms = new MemoryStream();
        while (true)
        {
            var result = await pipe.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var segment in result.Buffer)
            {
                ms.Write(segment.Span);
            }
            pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                break;
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Acquires a typed channel writer over the pipe and writes every item in
    /// <paramref name="items"/>, then completes the writer. Useful for invoking a server method
    /// that takes a stream of typed inputs.
    /// </summary>
    public static async ValueTask ChannelPublishAsync<T>(
        this IRentedNexusDuplexPipe pipe,
        IEnumerable<T> items,
        CancellationToken cancellationToken = default)
    {
        if (pipe is null) throw new ArgumentNullException(nameof(pipe));
        if (items is null) throw new ArgumentNullException(nameof(items));

        var writer = await pipe.GetChannelWriter<T>().ConfigureAwait(false);
        try
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Acquires a typed channel reader over the pipe, drains every item until the writer
    /// completes, and returns the list of received items.
    /// </summary>
    public static async ValueTask<List<T>> ChannelCollectAsync<T>(
        this IRentedNexusDuplexPipe pipe,
        CancellationToken cancellationToken = default)
    {
        if (pipe is null) throw new ArgumentNullException(nameof(pipe));

        var reader = await pipe.GetChannelReader<T>().ConfigureAwait(false);
        var collected = new List<T>();
        await foreach (var item in reader.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            collected.Add(item);
        }
        return collected;
    }
}
