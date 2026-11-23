namespace NexNet.Inspector.App.Model;

/// <summary>The two kinds of document a tab holds.</summary>
public enum DocumentKind : byte
{
    /// <summary>A live attach to one session of an inspector endpoint.</summary>
    Live = 0,

    /// <summary>One session of a recording on disk.</summary>
    File = 1,
}

/// <summary>Where a live document stands.</summary>
public enum LiveState : byte
{
    /// <summary>Attaching: the stream is being opened.</summary>
    Attaching = 0,

    /// <summary>Attached: frames arrive.</summary>
    Live = 1,

    /// <summary>The session disconnected; its data stays (the session-ended banner).</summary>
    Ended = 2,

    /// <summary>The user detached: the view was reset and holds nothing.</summary>
    Detached = 3,

    /// <summary>The attach failed or the endpoint went away mid-stream.</summary>
    Failed = 4,
}

/// <summary>What the user chose when closing a document with unsaved data (OQ5; the prompt itself is I29's).</summary>
public enum CloseDecision : byte
{
    /// <summary>Keep the document open.</summary>
    Cancel = 0,

    /// <summary>Close and drop the data.</summary>
    Discard = 1,

    /// <summary>Save, then close.</summary>
    Save = 2,
}

/// <summary>Where the Connect dialog's endpoint probe (OQ18) stands.</summary>
public enum ProbeState : byte
{
    /// <summary>Nothing to probe yet (no endpoint typed).</summary>
    Idle = 0,

    /// <summary>Waiting for the typing pause before probing.</summary>
    Pending = 1,

    /// <summary>Connecting to the endpoint.</summary>
    Probing = 2,

    /// <summary>Reached; the server's policy is known.</summary>
    Reached = 3,

    /// <summary>The text is not an endpoint address.</summary>
    Invalid = 4,

    /// <summary>Nothing answered.</summary>
    Unreachable = 5,

    /// <summary>The endpoint answered and refused the token.</summary>
    Refused = 6,
}

/// <summary>Where the Connect dialog's connect step stands.</summary>
public enum ConnectState : byte
{
    /// <summary>Not connected.</summary>
    Idle = 0,

    /// <summary>Connecting ("Connecting…" with a spinner).</summary>
    Connecting = 1,

    /// <summary>Connected; for a server, a session is picked next.</summary>
    Connected = 2,

    /// <summary>The connect or attach failed (the red status line).</summary>
    Failed = 3,
}
