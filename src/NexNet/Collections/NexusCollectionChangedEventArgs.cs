using System.Collections.Specialized;

namespace NexNet.Collections;

/// <summary>
/// Provides data for events that notify listeners of changes to a Nexus-backed collection.
/// </summary>
public class NexusCollectionChangedEventArgs
{
    /// <summary>
    /// Gets the type of change that occurred on the collection.
    /// </summary>
    /// <value>
    /// A <see cref="NotifyCollectionChangedAction"/> value indicating
    /// whether items were added, removed, replaced, moved, or the entire collection was reset.
    /// </value>
    public NexusCollectionChangedAction ChangedAction { get; init; }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="NexusCollectionChangedEventArgs"/> class
    /// with the default change action (typically <see cref="NotifyCollectionChangedAction.Reset"/>).
    /// </summary>
    public NexusCollectionChangedEventArgs()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusCollectionChangedEventArgs"/> class
    /// with the specified collection change action.
    /// </summary>
    /// <param name="action">
    /// The <see cref="NotifyCollectionChangedAction"/> value indicating
    /// the type of change that occurred on the collection.
    /// </param>
    public NexusCollectionChangedEventArgs(NexusCollectionChangedAction action)
    {
        ChangedAction = action;
    }
}

/// <summary>
/// Describes the action that caused a changed event
/// </summary>
public enum NexusCollectionChangedAction
{
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
}
