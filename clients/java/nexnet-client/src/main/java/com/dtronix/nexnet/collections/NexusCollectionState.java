package com.dtronix.nexnet.collections;

/**
 * The connection state of a synchronized collection.
 */
public enum NexusCollectionState {
    /** Not set. */
    UNSET,
    /** Not connected to the server's collection. */
    DISCONNECTED,
    /** Connected and receiving changes. */
    CONNECTED
}
