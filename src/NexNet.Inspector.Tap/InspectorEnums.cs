namespace NexNet.Inspector.Tap;

/// <summary>What a tap does when a viewer or recorder falls behind and its ring is full.</summary>
public enum InspectorOverflow : byte
{
    /// <summary>
    /// Discard the transport ops that do not fit and report them as a gap (dropped op and byte counts, tick range). The
    /// inspected connection is never slowed.
    /// </summary>
    Drop = 0,

    /// <summary>
    /// Hold the inspected connection's read or flush until the ring has room: lossless, but the connection runs at the
    /// viewer's pace.
    /// </summary>
    Block = 1,
}

/// <summary>What an attach targets.</summary>
public enum InspectorTargetKind : byte
{
    /// <summary>One session of a server, by ID (<see cref="InspectorAttachRequest.SessionId"/>).</summary>
    Session = 0,

    /// <summary>The single connection of a client; the session ID is ignored.</summary>
    Client = 1,
}

/// <summary>Outcome of an inspector endpoint request.</summary>
public enum InspectorStatus : byte
{
    /// <summary>Accepted.</summary>
    Ok = 0,

    /// <summary>No live session has the requested ID.</summary>
    NoSuchSession = 1,

    /// <summary>A client target was requested and the client has no live connection.</summary>
    NoConnection = 2,

    /// <summary>The target kind does not match the inspected side (a client target on a server, or the reverse).</summary>
    WrongTarget = 3,

    /// <summary>Recording was requested and the inspected side's configuration does not allow it.</summary>
    RecordingDisabled = 4,

    /// <summary>The endpoint session already streams; detach first.</summary>
    Busy = 5,

    /// <summary>No attach or record request was accepted before the stream was opened.</summary>
    NotArmed = 6,

    /// <summary>The inspector was disposed.</summary>
    Unavailable = 7,
}

/// <summary>Where a tapped session stands.</summary>
public enum InspectorSessionState : byte
{
    /// <summary>The transport is connected and the handshake has not completed.</summary>
    Handshaking = 0,

    /// <summary>The handshake completed.</summary>
    Connected = 1,
}
