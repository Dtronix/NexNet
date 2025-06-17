using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Logging;

namespace NexNet.Collections;

/// <summary>
/// A high-throughput event without multicast delegates.
/// <para>
/// <see cref="Subscribe(Action{T})"/> / <see cref="ISubscriptionEvent{T}.Subscribe"/> and the disposable token they
/// return take a short lock to modify the immutable handler array, while
/// <see cref="Raise"/> is completely lock‑free to maximise throughput when firing
/// the event.
/// </para>
/// </summary>
internal class SubscriptionEvent<T> : ISubscriptionEvent<T>
{
    // Volatile so that readers always see the latest array reference.
    private volatile Action<T>[] _handlers = [];
    private readonly Lock _sync = new();

    /// <summary>
    /// Optional logger used when an individual handler throws an exception.
    /// </summary>
    public INexusLogger? Logger;

    /// <summary>
    /// Subscribes a handler to the event and returns an <see cref="IDisposable"/> token.
    /// Disposing the token will automatically unsubscribe the handler.
    /// </summary>
    /// <param name="handler">The delegate to be invoked when the event is raised.</param>
    /// <returns>An <see cref="IDisposable"/> that removes the handler upon disposal.</returns>
    public IDisposable Subscribe(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            var old = _handlers;
            var next = new Action<T>[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = handler;
            _handlers = next;
        }
        return new Subscription(this, handler);
    }

    /// <summary>
    /// Raises the event for all currently subscribed handlers without taking any locks.
    /// </summary>
    /// <param name="args">The argument to pass to each handler.</param>
    public void Raise(T args)
    {
        // ReSharper disable once InconsistentlySynchronizedField
        var snapshot = _handlers;

        for (int i = 0; i < snapshot.Length; i++)
        {
            var local = snapshot[i];
            Task.Factory.StartNew(static state =>
            {
                var (handler, arguments, logger) = ((Action<T>, T, INexusLogger?))state!;
                try
                {
                    handler(arguments); 
                }
                catch (Exception e)
                {
                    logger?.LogWarning(e, "Event raised an exception");
                }
            }, (local, args, Logger), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Unsubscribes the specified <paramref name="handler"/> from the event.
    /// </summary>
    /// <remarks>
    /// Called by the <see cref="Subscription.Dispose"/> implementation when the
    /// disposable token returned by <see cref="Subscribe(Action{T})"/> is disposed.
    /// The method takes a lock to construct a new handler array without the
    /// removed delegate, ensuring readers always see an immutable snapshot.
    /// </remarks>
    /// <param name="handler">The handler delegate to remove.</param>
    private void Unsubscribe(Action<T> handler)
    {
        lock (_sync)
        {
            var old = _handlers;
            int idx = Array.IndexOf(old, handler);
            if (idx < 0) return; // already removed
            if (old.Length == 1)
            {
                _handlers = [];
                return;
            }

            var next = new Action<T>[old.Length - 1];
            // copy before the removed index
            if (idx > 0)
                Array.Copy(old, 0, next, 0, idx);
            // copy after the removed index
            if (idx < old.Length - 1)
                Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);

            _handlers = next;
        }
    }

    /// <summary>
    /// Disposable token that removes its associated handler from the parent
    /// <see cref="SubscriptionEvent{T}"/> when disposed.
    /// </summary>
    private class Subscription : IDisposable
    {
        private readonly SubscriptionEvent<T> _parent;
        private readonly Action<T> _handler;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="Subscription"/> class.
        /// </summary>
        /// <param name="parent">The parent event.</param>
        /// <param name="handler">The handler associated with this token.</param>
        public Subscription(SubscriptionEvent<T> parent, Action<T> handler)
        {
            _parent  = parent;
            _handler = handler;
        }
        
        /// <summary>
        /// Disposes the subscription. The first call removes the handler; subsequent
        /// calls are ignored.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _parent.Unsubscribe(_handler);
            _disposed = true;
        }
    }
}

/// <summary>
/// A non‑generic, parameter‑less equivalent of <see cref="SubscriptionEvent{T}"/>.
/// </summary>
internal class SubscriptionEvent : ISubscriptionEvent
{
    private volatile Action[] _handlers = [];
    private readonly Lock _sync = new Lock();
    
    /// <summary>
    /// Optional logger used when an individual handler throws an exception.
    /// </summary>
    public INexusLogger? Logger;

    /// <summary>
    /// Subscribes a parameter‑less handler to the event.
    /// </summary>
    /// <param name="handler">The delegate to invoke when the event is raised.</param>
    /// <returns>An <see cref="IDisposable"/> token that removes the handler on disposal.</returns>
    public IDisposable Subscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            var old = _handlers;
            var next = new Action[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = handler;
            _handlers = next;
        }

        return new Subscription(this, handler);
    }

    /// <summary>
    /// Raises the event for all currently subscribed handlers without taking any locks.
    /// </summary>
    public void Raise()
    {
        // ReSharper disable once InconsistentlySynchronizedField
        var snapshot = _handlers;
        for (int i = 0; i < snapshot.Length; i++)
        {
            var local = snapshot[i];
            Task.Factory.StartNew(static state =>
            {
                var (handler, logger) = ((Action, INexusLogger?))state!;
                try
                {
                    handler();

                }
                catch (Exception e)
                {
                    logger?.LogWarning(e, "Event raised an exception");
                }
            }, (local, Logger), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Removes the specified handler delegate from the subscription list.
    /// </summary>
    /// <param name="handler">The handler to remove.</param>
    private void Unregister(Action handler)
    {
        lock (_sync)
        {
            var old = _handlers;
            int idx = Array.IndexOf(old, handler);
            if (idx < 0) return;
            if (old.Length == 1)
            {
                _handlers = [];
                return;
            }

            var next = new Action[old.Length - 1];
            if (idx > 0)
                Array.Copy(old, 0, next, 0, idx);
            if (idx < old.Length - 1)
                Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);

            _handlers = next;
        }
    }
    
    /// <summary>
    /// Disposable token that removes its associated handler from the parent
    /// <see cref="SubscriptionEvent"/> when disposed.
    /// </summary>
    private class Subscription : IDisposable
    {
        private readonly SubscriptionEvent _parent;
        private readonly Action _handler;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="Subscription"/> class.
        /// </summary>
        /// <param name="parent">The parent event.</param>
        /// <param name="handler">The handler associated with this token.</param>
        public Subscription(SubscriptionEvent parent, Action handler)
        {
            _parent = parent;
            _handler = handler;
        }

        /// <summary>
        /// Disposes the subscription. The first call unregisters the handler; subsequent
        /// calls are ignored.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _parent.Unregister(_handler);
            _disposed = true;
        }
    }
}
