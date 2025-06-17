using System;

namespace NexNet.Collections;

/// <summary>
/// Defines a subscribable event that clients can register handlers with,
/// and later unsubscribe by disposing the returned token.
/// </summary>
/// <typeparam name="T">
/// The type of the argument passed to each handler when the event is raised.
/// </typeparam>
public interface ISubscriptionEvent<out T>
{
    /// <summary>
    /// Registers a handler to be invoked when the event is raised.
    /// </summary>
    /// <param name="handler">
    /// The <see cref="Action{T}"/> delegate to invoke when the event occurs.
    /// </param>
    /// <returns>
    /// An <see cref="IDisposable"/> token; calling <c>Dispose()</c> on this token
    /// will unregister the handler so it no longer receives event notifications.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="handler"/> is <c>null</c>.
    /// </exception>
    IDisposable Subscribe(Action<T> handler);
}


/// <summary>
/// Defines a subscribable event that clients can register handlers with,
/// and later unsubscribe by disposing the returned token.
/// </summary>
public interface ISubscriptionEvent
{
    /// <summary>
    /// Registers a handler to be invoked when the event is raised.
    /// </summary>
    /// <param name="handler">
    /// The <see cref="Action"/> delegate to invoke when the event occurs.
    /// </param>
    /// <returns>
    /// An <see cref="IDisposable"/> token; calling <c>Dispose()</c> on this token
    /// will unregister the handler so it no longer receives event notifications.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="handler"/> is <c>null</c>.
    /// </exception>
    IDisposable Subscribe(Action handler);
}
