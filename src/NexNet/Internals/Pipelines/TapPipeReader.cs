using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Diagnostics;
using NexNet.Logging;

namespace NexNet.Internals.Pipelines;

/// <summary>
/// Session input that reports to an <see cref="INexusSessionTap"/> the bytes each read adds. Only installed when a tap
/// is configured; otherwise the session reads the transport's reader directly.
/// </summary>
internal sealed class TapPipeReader : PipeReader
{
    private readonly PipeReader _inner;
    private readonly INexusLogger? _logger;

    // Cleared when the tap fails: the session keeps running untapped.
    private INexusSessionTap? _tap;

    // The buffer of the last read, and how many of its bytes, from its start, the tap has already seen. A read returns
    // the unconsumed remainder of the previous one followed by new bytes; only the new bytes are reported.
    private ReadOnlySequence<byte> _buffer;
    private long _reported;

    public TapPipeReader(PipeReader inner, INexusSessionTap tap, INexusLogger? logger)
    {
        _inner = inner;
        _tap = tap;
        _logger = logger;
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var read = _inner.ReadAsync(cancellationToken);
        if (_tap == null)
            return read;

        if (!read.IsCompletedSuccessfully)
            return ReadAsyncSlow(read);

        var result = read.Result;
        var tapped = Report(result.Buffer);
        return tapped.IsCompletedSuccessfully
            ? new ValueTask<ReadResult>(result)
            : WaitForTap(tapped, result);
    }

    public override bool TryRead(out ReadResult result)
    {
        if (!_inner.TryRead(out result))
            return false;

        // TryRead cannot wait: a tap that holds the read only gets its failures observed.
        if (_tap != null)
        {
            var tapped = Report(result.Buffer);
            if (!tapped.IsCompletedSuccessfully)
                _ = Observe(tapped);
        }

        return true;
    }

    public override void AdvanceTo(SequencePosition consumed)
        => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        // Measured before the inner reader may recycle the segments: what is left after consumed comes back at the
        // start of the next read, and the tap has seen it.
        if (_tap != null)
            _reported = _buffer.Slice(consumed).Length;
        _buffer = default;
        _inner.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null)
    {
        _buffer = default;
        _inner.Complete(exception);
    }

    private ValueTask Report(ReadOnlySequence<byte> buffer)
    {
        _buffer = buffer;
        var seen = _reported;
        _reported = buffer.Length;
        if (buffer.Length <= seen)
            return default;

        try
        {
            return _tap!.OnRead(seen == 0 ? buffer : buffer.Slice(seen), Stopwatch.GetTimestamp());
        }
        catch (Exception e)
        {
            Fail(e);
            return default;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ReadResult> ReadAsyncSlow(ValueTask<ReadResult> read)
    {
        var result = await read.ConfigureAwait(false);
        if (_tap == null)
            return result;

        var tapped = Report(result.Buffer);
        if (!tapped.IsCompletedSuccessfully)
            await Observe(tapped).ConfigureAwait(false);
        return result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ReadResult> WaitForTap(ValueTask tapped, ReadResult result)
    {
        await Observe(tapped).ConfigureAwait(false);
        return result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask Observe(ValueTask tapped)
    {
        try
        {
            await tapped.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private void Fail(Exception e)
    {
        _tap = null;
        _logger?.LogError(e, "Transport tap failed on a read; this session's reads are no longer tapped.");
    }
}
