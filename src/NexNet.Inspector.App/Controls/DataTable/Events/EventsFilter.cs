namespace NexNet.Inspector.App.Controls.DataTable.Events;

/// <summary>Which frames the Events table lists (the pane header's Filter ▾).</summary>
public enum EventsFilter : byte
{
    /// <summary>Every frame of both directions.</summary>
    All = 0,

    /// <summary>Only frames the tapped endpoint read (▼ IN).</summary>
    Inbound = 1,

    /// <summary>Only frames the tapped endpoint wrote (▲ OUT).</summary>
    Outbound = 2,
}
