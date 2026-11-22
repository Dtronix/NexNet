using NexNet.Messages;

namespace NexNet.Interop;

/// <summary>
/// The state changes a client under test raised, in order, and the reconnection attempts it made, since it was created
/// or since the last reset (<see cref="IClientUnderTest.GetStateHistoryAsync"/>, control protocol §6.4
/// <c>client.stateHistory</c>).
/// </summary>
/// <param name="States">
/// Every state the client reported (C# <c>StateChanged</c>, Java state listeners, JS <c>statechange</c>), in order. The
/// state the client is created in is not a change and is not listed.
/// </param>
/// <param name="ReconnectAttempts">
/// The attempts the client's reconnection loops started (C# <c>IReconnectionPolicy.Reconnection</c>, Java
/// <c>ReconnectListener.onAttempt</c>, JS <c>reconnectattempt</c>), over every loop; 0 without a reconnection policy.
/// </param>
public sealed record ClientStateHistory(IReadOnlyList<ConnectionState> States, int ReconnectAttempts);
