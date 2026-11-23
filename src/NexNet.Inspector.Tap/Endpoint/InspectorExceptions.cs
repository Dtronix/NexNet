using NexNet.Messages;

namespace NexNet.Inspector.Tap;

/// <summary>Connecting to an inspector endpoint failed: unreachable, or the token was refused.</summary>
public sealed class InspectorConnectionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public InspectorConnectionException(ConnectionResult.StateValue state, DisconnectReason reason, Exception? inner)
        : base($"Could not connect to the inspector endpoint: {state} ({reason}).", inner)
    {
        State = state;
        Reason = reason;
    }

    /// <summary>The connection attempt's outcome.</summary>
    public ConnectionResult.StateValue State { get; }

    /// <summary>Why the endpoint disconnected; <see cref="DisconnectReason.Authentication"/> for a wrong token.</summary>
    public DisconnectReason Reason { get; }
}

/// <summary>The inspector endpoint refused a request.</summary>
public sealed class InspectorRequestException : Exception
{
    /// <summary>Creates the exception.</summary>
    public InspectorRequestException(InspectorStatus status)
        : base($"The inspector endpoint refused the request: {status}.")
    {
        Status = status;
    }

    /// <summary>Why it was refused.</summary>
    public InspectorStatus Status { get; }
}
