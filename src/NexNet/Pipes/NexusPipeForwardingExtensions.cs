using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes;

/// <summary>
/// Byte-level relays between pipes and channels, for relay and gateway servers. A forward reads the source and writes
/// the destination one buffer at a time and waits for the destination's send credit before reading again, so a slow
/// destination stalls the source instead of buffering on the relay.
/// </summary>
public static class NexusPipeForwardingExtensions
{
    /// <summary>
    /// Copies everything <paramref name="source"/> receives to <paramref name="destination"/> until the source's input
    /// completes, then completes the destination's output (half-close). The other direction of both pipes is left as is.
    /// </summary>
    /// <param name="source">Pipe whose input is read.</param>
    /// <param name="destination">Pipe whose output is written.</param>
    /// <param name="cancellationToken">Stops the forward. Neither pipe is completed; data already forwarded stays
    /// delivered and the source's input is positioned after it.</param>
    /// <returns>A task which completes when the source's input completed, or the destination's output was completed.
    /// </returns>
    /// <exception cref="OperationCanceledException">The forward was canceled.</exception>
    public static ValueTask ForwardToAsync(
        this INexusDuplexPipe source,
        INexusDuplexPipe destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        return CopyAsync(source, destination, null, cancellationToken);
    }

    /// <summary>
    /// Forwards <paramref name="a"/> to <paramref name="b"/> and <paramref name="b"/> to <paramref name="a"/>
    /// concurrently. Each direction half-closes its destination when its source completes; the bridge returns once both
    /// directions finished. If either pipe ends abnormally (reset, disconnect, <see cref="INexusDuplexPipe.CompleteAsync"/>)
    /// while a direction still runs, both pipes are completed and the bridge returns.
    /// </summary>
    /// <param name="a">First pipe.</param>
    /// <param name="b">Second pipe.</param>
    /// <param name="cancellationToken">Stops both directions without completing either pipe.</param>
    /// <returns>A task which completes when the bridge ended.</returns>
    /// <exception cref="OperationCanceledException">The bridge was canceled.</exception>
    public static async ValueTask BridgeAsync(
        this INexusDuplexPipe a,
        INexusDuplexPipe b,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var aToBState = new CopyState();
        var bToAState = new CopyState();
        var aToB = CopyAsync(a, b, null, stop.Token, aToBState).AsTask();
        var bToA = CopyAsync(b, a, null, stop.Token, bToAState).AsTask();
        var aComplete = a.CompleteTask;
        var bComplete = b.CompleteTask;
        var waitOn = new List<Task>(4) { aToB, bToA, aComplete, bComplete };

        Task ended;
        while (true)
        {
            if (aToB.IsCompletedSuccessfully && bToA.IsCompletedSuccessfully)
                return;

            var done = await Task.WhenAny(waitOn).ConfigureAwait(false);
            waitOn.Remove(done);

            if (done == aToB || done == bToA)
            {
                if (done.IsCompletedSuccessfully)
                    continue;

                // A direction failed or was canceled.
                ended = done;
                break;
            }

            // A pipe is complete. After a normal half-close the reverse direction may still be draining the data it
            // buffered, so this is an abnormal end only while the direction writing to that pipe has not closed it.
            var writer = done == aComplete ? bToAState : aToBState;
            if (writer.HalfClosed)
                continue;

            ended = done;
            break;
        }

        if (cancellationToken.IsCancellationRequested && ended.IsCanceled && ended != aComplete && ended != bComplete)
        {
            // The caller's cancellation: stop the other direction, complete neither pipe.
            await StopAsync(stop, aToB, bToA).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }

        await a.CompleteAsync().ConfigureAwait(false);
        await b.CompleteAsync().ConfigureAwait(false);
        await StopAsync(stop, aToB, bToA).ConfigureAwait(false);

        // A direction that threw: rethrow its exception. A pipe that ended is a normal return.
        if (ended != aComplete && ended != bComplete)
            await ended.ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards the items <paramref name="source"/> receives to <paramref name="destination"/> as raw bytes, without
    /// deserializing or serializing them, until the source completes; then completes the destination's output
    /// (half-close). Items stream through as they arrive, so an item larger than the pipe window is not buffered whole.
    /// Do not write to the destination's channel writer while the forward runs.
    /// </summary>
    /// <typeparam name="T">The channels' item type.</typeparam>
    /// <param name="source">Channel whose received items are forwarded.</param>
    /// <param name="destination">Channel the items are sent on.</param>
    /// <param name="cancellationToken">Stops the forward. Neither channel is completed.</param>
    /// <returns>A task which completes when the source completed, or the destination's output was completed.</returns>
    /// <exception cref="ArgumentException">A channel has no underlying pipe.</exception>
    /// <exception cref="NexusMessageTooLargeException">An item is larger than the destination accepts. None of it was
    /// sent; the items before it were. Neither channel is completed, and the source's next read returns that item.
    /// </exception>
    /// <exception cref="OperationCanceledException">The forward was canceled.</exception>
    public static ValueTask ForwardToAsync<T>(
        this INexusDuplexChannel<T> source,
        INexusDuplexChannel<T> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var sourcePipe = source.BasePipe
                         ?? throw new ArgumentException("The source channel has no underlying pipe.", nameof(source));
        var destinationPipe = destination.BasePipe
                              ?? throw new ArgumentException("The destination channel has no underlying pipe.", nameof(destination));

        var maxItemSize = (destinationPipe as NexusDuplexPipe)?.Session?.SendMaxMessageSize ?? int.MaxValue;
        return CopyAsync(sourcePipe, destinationPipe, maxItemSize, cancellationToken);
    }

    private static async Task StopAsync(CancellationTokenSource stop, params Task[] directions)
    {
        stop.Cancel();
        foreach (var direction in directions)
        {
            try
            {
                await direction.ConfigureAwait(false);
            }
            catch
            {
                // Stopped by the bridge; its outcome no longer matters.
            }
        }
    }

    /// <summary>
    /// The copy loop. With <paramref name="maxItemSize"/> the data is channel framing: each item's length prefix is
    /// parsed and checked against it as the bytes pass through.
    /// </summary>
    private static async ValueTask CopyAsync(
        INexusDuplexPipe source,
        INexusDuplexPipe destination,
        int? maxItemSize,
        CancellationToken cancellationToken,
        CopyState? state = null)
    {
        var input = source.Input;
        var output = destination.Output;

        // Framed copy: bytes of the current item still to pass through.
        long itemRemaining = 0;

        while (true)
        {
            var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (result.IsCanceled)
            {
                input.AdvanceTo(buffer.Start);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("The read of the source was canceled.");
            }

            long copyLength;
            NexusMessageTooLargeException? tooLarge = null;
            if (maxItemSize is not { } max)
            {
                copyLength = buffer.Length;
            }
            else
            {
                var reader = new SequenceReader<byte>(buffer);
                while (!reader.End)
                {
                    if (itemRemaining > 0)
                    {
                        var take = Math.Min(itemRemaining, reader.Remaining);
                        reader.Advance(take);
                        itemRemaining -= take;
                        continue;
                    }

                    var prefixStart = reader.Consumed;
                    if (!ChannelFraming.TryReadLength(ref reader, out var length))
                        break; // The prefix is split; parse it again with more data.

                    if (length > max)
                    {
                        tooLarge = new NexusMessageTooLargeException(length, max);
                        reader.Rewind(reader.Consumed - prefixStart);
                        break;
                    }

                    itemRemaining = length;
                }

                // At the end of the source an incomplete item is passed on as it is.
                copyLength = tooLarge == null && result.IsCompleted ? buffer.Length : reader.Consumed;
            }

            var copy = buffer.Slice(0, copyLength);
            FlushResult flush = default;
            if (copyLength > 0)
            {
                foreach (var segment in copy)
                    output.Write(segment.Span);

                try
                {
                    flush = await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    input.AdvanceTo(copy.End);
                    throw;
                }
            }

            if (tooLarge != null)
            {
                // Consumed and examined at the oversized item's prefix: the next read returns it.
                input.AdvanceTo(copy.End);
                throw tooLarge;
            }

            // Keep what was not copied (a split prefix) and wait for more data after it.
            input.AdvanceTo(copy.End, buffer.End);

            if (flush.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("The flush to the destination was canceled.");
            }

            if (flush.IsCompleted)
                return;

            if (result.IsCompleted)
            {
                if (state != null)
                    state.HalfClosed = true;
                await output.CompleteAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// What a bridge needs to know about one of its directions.
    /// </summary>
    private sealed class CopyState
    {
        /// <summary>
        /// Set just before the direction completes its destination's output after the source completed.
        /// </summary>
        public volatile bool HalfClosed;
    }
}
