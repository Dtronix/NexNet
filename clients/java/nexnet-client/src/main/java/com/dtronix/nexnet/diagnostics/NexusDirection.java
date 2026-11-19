package com.dtronix.nexnet.diagnostics;

/** Whether data flows to this side or from it. Mirrors the C# {@code NexusDirection}. */
public enum NexusDirection {
    /** The peer is sending to this side. */
    INBOUND,
    /** This side is sending to the peer. */
    OUTBOUND
}
