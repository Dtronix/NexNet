using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;

namespace NexNet.Collections;

/// <summary>
/// Provides data for events that notify listeners of changes to a Nexus-backed collection.
/// </summary>
public class NexusCollectionChangedEventArgs
{
    private static readonly ConcurrentBag<NexusCollectionChangedEventArgs> _pool = new();

    /// <summary>
    /// Gets the type of change that occurred on the collection.
    /// </summary>
    /// <value>
    /// A <see cref="NotifyCollectionChangedAction"/> value indicating
    /// whether items were added, removed, replaced, moved, or the entire collection was reset.
    /// </value>
    public NexusCollectionChangedAction ChangedAction { get; internal set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusCollectionChangedEventArgs"/> class
    /// with the default change action (typically <see cref="NotifyCollectionChangedAction.Reset"/>).
    /// </summary>
    private NexusCollectionChangedEventArgs()
    {
    }

    /// <summary>
    /// Rents a <see cref="NexusCollectionChangedEventArgs"/> instance from the pool.
    /// </summary>
    /// <param name="action">The collection change action.</param>
    /// <returns>An <see cref="Owner"/> struct that owns the rented instance.</returns>
    public static Owner Rent(NexusCollectionChangedAction action = default)
    {
        if (!_pool.TryTake(out var args))
        {
            args = new NexusCollectionChangedEventArgs();
        }

        args.ChangedAction = action;
        return new Owner(args);
    }

    /// <summary>
    /// Returns the instance to the pool.
    /// </summary>
    /// <param name="args">The instance to return.</param>
    private static void Return(NexusCollectionChangedEventArgs args)
    {
        args.ChangedAction = default;
        _pool.Add(args);
    }

    /// <summary>
    /// Disposable owner struct that manages the lifetime of a rented <see cref="NexusCollectionChangedEventArgs"/>.
    /// </summary>
    public readonly struct Owner : IDisposable
    {
        /// <summary>
        /// Gets the owned <see cref="NexusCollectionChangedEventArgs"/> instance.
        /// </summary>
        public NexusCollectionChangedEventArgs Value { get; }

        internal Owner(NexusCollectionChangedEventArgs value)
        {
            Value = value;
        }

        /// <summary>
        /// Returns the owned instance to the pool.
        /// </summary>
        public void Dispose()
        {
            if (Value != null)
            {
                Return(Value);
            }
        }
    }
}

/// <summary>
/// Describes the action that caused a changed event
/// </summary>
public enum NexusCollectionChangedAction
{
    /// <summary>Action was unset.</summary>
    Unset,
    /// <summary>An item was added to the collection.</summary>
    Add,
    /// <summary>An item was removed from the collection.</summary>
    Remove,
    /// <summary>An item was replaced in the collection.</summary>
    Replace,
    /// <summary>An item was moved within the collection.</summary>
    Move,
    /// <summary>The contents of the collection changed dramatically.</summary>
    Reset,
    /// <summary>The contents of the collection is ready for use.</summary>
    Ready,
}
