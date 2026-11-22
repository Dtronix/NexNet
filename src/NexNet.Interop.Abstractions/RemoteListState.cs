using NexNet.Collections;

namespace NexNet.Interop;

/// <summary>
/// What a client's synchronized collection reports about itself (control protocol §6.10 <c>list.state</c>), read from
/// the client runtime when asked.
/// </summary>
/// <param name="Mode">The collection's mode, as the client runtime reports it (C#: the mode it was declared with).</param>
/// <param name="State">The collection's state: <see cref="NexusCollectionState.Connected"/> once its initial state arrived.</param>
/// <param name="IsDisabled">
/// True when the runtime's disabled signal has completed (C# <c>DisabledTask</c>, Java <c>disabled()</c>, JavaScript
/// <c>disabled</c>): the collection was disabled or its session ended. Also true before the first enable.
/// </param>
/// <param name="IsReadOnly">
/// The runtime's own read-only flag (C# <c>INexusList.IsReadOnly</c>); null for runtimes that have none (Java,
/// JavaScript), whose <paramref name="Mode"/> tells whether the client can change the collection.
/// </param>
public sealed record RemoteListState(RemoteCollectionMode Mode, NexusCollectionState State, bool IsDisabled, bool? IsReadOnly);
