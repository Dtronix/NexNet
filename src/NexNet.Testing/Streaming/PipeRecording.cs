using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Testing.Streaming;

/// <summary>
/// Observation surface for a tapped duplex pipe. The harness's <c>TestPipeFactory</c> creates
/// one of these per wrapped pipe; assertions read its current bytes and lifecycle state, and
/// can await activity via <see cref="WaitForBytesAsync"/> and <see cref="WaitForCompletionAsync"/>.
/// </summary>
public sealed class PipeRecording
{
    private readonly object _gate = new();
    private readonly ArrayBufferWriter<byte> _consumed = new();
    private readonly ArrayBufferWriter<byte> _written = new();
    private bool _completed;
    private Exception? _faultedWith;
    private TaskCompletionSource _bytesSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _completionSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Bytes the local handler has consumed (advanced past) from the pipe.</summary>
    public ReadOnlyMemory<byte> ConsumedBytes
    {
        get
        {
            lock (_gate) { return _consumed.WrittenMemory.ToArray(); }
        }
    }

    /// <summary>Bytes the local handler has flushed into the pipe's output.</summary>
    public ReadOnlyMemory<byte> WrittenBytes
    {
        get
        {
            lock (_gate) { return _written.WrittenMemory.ToArray(); }
        }
    }

    /// <summary>Whether the underlying pipe has reached the completed state.</summary>
    public bool IsCompleted
    {
        get { lock (_gate) { return _completed; } }
    }

    /// <summary>Exception that closed the pipe, if any.</summary>
    public Exception? FaultedWith
    {
        get { lock (_gate) { return _faultedWith; } }
    }

    /// <summary>
    /// Returns a task that completes when at least <paramref name="minBytes"/> total bytes
    /// have been consumed-or-written (whichever side the caller is interested in is up to the
    /// caller — this signal fires on either).
    /// </summary>
    public Task WaitForBytesAsync(int minBytes, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TaskCompletionSource signal;
            lock (_gate)
            {
                if (_consumed.WrittenCount >= minBytes || _written.WrittenCount >= minBytes)
                    return Task.CompletedTask;
                signal = _bytesSignal;
            }

            return AwaitAndRetry(signal, minBytes, cancellationToken);
        }
    }

    private async Task AwaitAndRetry(TaskCompletionSource signal, int minBytes, CancellationToken ct)
    {
        var task = ct.CanBeCanceled ? signal.Task.WaitAsync(ct) : signal.Task;
        await task.ConfigureAwait(false);
        await WaitForBytesAsync(minBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Returns a task that completes when the pipe enters the completed state.</summary>
    public Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_completed) return Task.CompletedTask;
            signal = _completionSignal;
        }
        return cancellationToken.CanBeCanceled ? signal.Task.WaitAsync(cancellationToken) : signal.Task;
    }

    internal void RecordConsumed(ReadOnlySpan<byte> slice)
    {
        TaskCompletionSource toFire;
        lock (_gate)
        {
            _consumed.Write(slice);
            toFire = _bytesSignal;
            _bytesSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        toFire.TrySetResult();
    }

    internal void RecordWritten(ReadOnlySpan<byte> slice)
    {
        TaskCompletionSource toFire;
        lock (_gate)
        {
            _written.Write(slice);
            toFire = _bytesSignal;
            _bytesSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        toFire.TrySetResult();
    }

    internal void RecordCompletion(Exception? error)
    {
        TaskCompletionSource toFire;
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _faultedWith = error;
            toFire = _completionSignal;
        }
        toFire.TrySetResult();
    }
}
