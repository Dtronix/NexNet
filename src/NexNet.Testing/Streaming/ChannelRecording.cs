using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Testing.Streaming;

/// <summary>
/// Observation surface for a tapped channel of <typeparamref name="T"/> items. Lives next to
/// <see cref="PipeRecording"/> as the typed counterpart: byte-level taps capture pipe traffic;
/// channel taps capture deserialized item traffic, which is the level of detail tests usually
/// want when asserting on channel-based methods.
/// </summary>
/// <typeparam name="T">Item type produced or consumed on the channel.</typeparam>
public sealed class ChannelRecording<T>
{
    private readonly object _gate = new();
    private readonly List<T> _items = new();
    private bool _completed;
    private Exception? _faultedWith;
    private TaskCompletionSource _itemSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _completionSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Snapshot of items recorded so far.</summary>
    public IReadOnlyList<T> Items
    {
        get { lock (_gate) { return _items.ToArray(); } }
    }

    /// <summary>Whether the channel has been completed.</summary>
    public bool IsCompleted
    {
        get { lock (_gate) { return _completed; } }
    }

    /// <summary>Exception that closed the channel, if any.</summary>
    public Exception? FaultedWith
    {
        get { lock (_gate) { return _faultedWith; } }
    }

    /// <summary>Returns a task that completes once <paramref name="minItems"/> items are recorded.</summary>
    public Task WaitForCountAsync(int minItems, CancellationToken cancellationToken = default)
        => WaitForCountCore(minItems, cancellationToken);

    private async Task WaitForCountCore(int minItems, CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource signal;
            lock (_gate)
            {
                if (_items.Count >= minItems) return;
                signal = _itemSignal;
            }
            var task = cancellationToken.CanBeCanceled ? signal.Task.WaitAsync(cancellationToken) : signal.Task;
            await task.ConfigureAwait(false);
        }
    }

    /// <summary>Returns a task that completes when the channel enters the completed state.</summary>
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

    /// <summary>Records an item and signals any pending count waiters.</summary>
    public void RecordItem(T item)
    {
        TaskCompletionSource toFire;
        lock (_gate)
        {
            _items.Add(item);
            toFire = _itemSignal;
            _itemSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        toFire.TrySetResult();
    }

    /// <summary>Records channel completion with an optional fault.</summary>
    public void RecordCompletion(Exception? error)
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
