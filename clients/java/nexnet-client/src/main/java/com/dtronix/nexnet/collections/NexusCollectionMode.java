package com.dtronix.nexnet.collections;

/**
 * The synchronization mode of a collection, as in the C# {@code NexusCollectionMode}. The mode is not sent on the
 * wire: both sides take it from the {@code [NexusCollection(mode)]} attribute of the server nexus (protocol
 * specification section 12.2).
 */
public enum NexusCollectionMode {
    /** Not set. Treated as read-only. */
    UNSET,
    /** Changes flow from the server to the client only. Client mutations fail locally and send nothing. */
    SERVER_TO_CLIENT,
    /** Changes flow both ways. Client mutations are sent to the server as requests. */
    BI_DIRECTIONAL,
    /** The server mirrors a collection from another server. Read-only for clients, like {@link #SERVER_TO_CLIENT}. */
    RELAY;

    /**
     * Whether a client may send change requests in this mode.
     *
     * @return true for {@link #BI_DIRECTIONAL} only
     */
    public boolean isWritable() {
        return this == BI_DIRECTIONAL;
    }
}
