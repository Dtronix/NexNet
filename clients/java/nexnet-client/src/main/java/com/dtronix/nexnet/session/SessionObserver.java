package com.dtronix.nexnet.session;

/**
 * Learns of every session as it starts (its transport connected), before it reads or writes anything. Registered
 * process-wide with {@link SessionObservers#add}; the metrics layer uses it to count sessions from the transport
 * connection, the handshake included. Runtime-internal.
 */
@FunctionalInterface
public interface SessionObserver {
    /**
     * Called synchronously from {@link NexusSession#start()}, on the thread that starts the session. Must be cheap
     * and must not throw (a throw is ignored).
     *
     * @param session the session
     */
    void started(NexusSession session);
}
