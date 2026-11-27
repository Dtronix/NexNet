namespace NexNet.Inspector.App.Model;

/// <summary>The views a lower (stream) pane of the Inspect workspace can show; either pane shows any of them.</summary>
public enum StreamPaneKind : byte
{
    /// <summary>The Events table (I20).</summary>
    Events = 0,

    /// <summary>The Ladder (I21).</summary>
    Ladder = 1,

    /// <summary>The Hex stream (I22).</summary>
    Hex = 2,
}

/// <summary>Which of the two lower panes.</summary>
public enum PaneSide : byte
{
    /// <summary>The left pane: always shown; its split button toggles the right one.</summary>
    Left = 0,

    /// <summary>The right pane: closed by its ✕, by Single, or when the window is narrower than 1200 px (OQ10).</summary>
    Right = 1,
}

/// <summary>The inspector pane's tabs.</summary>
public enum InspectorTab : byte
{
    /// <summary>The decoded property tree and the assembly bar.</summary>
    Decoded = 0,

    /// <summary>"Message trace" (or "Handshake" on a handshake frame): its content is I25's.</summary>
    Trace = 1,
}
