package com.dtronix.nexnet.session;

import com.dtronix.nexnet.Registration;
import java.util.concurrent.CopyOnWriteArrayList;

/**
 * The process-wide {@link SessionObserver}s. Runtime-internal.
 */
public final class SessionObservers {
    private static final CopyOnWriteArrayList<SessionObserver> OBSERVERS = new CopyOnWriteArrayList<>();

    private SessionObservers() {
    }

    /**
     * Adds an observer of session starts.
     *
     * @param observer the observer
     * @return a registration that removes it
     */
    public static Registration add(SessionObserver observer) {
        if (observer == null) {
            throw new NullPointerException("observer");
        }
        OBSERVERS.add(observer);
        return () -> OBSERVERS.remove(observer);
    }

    /** Tells every observer that the session started. */
    static void started(NexusSession session) {
        for (SessionObserver observer : OBSERVERS) {
            try {
                observer.started(session);
            } catch (RuntimeException ignored) {
                // An observer must never break the session.
            }
        }
    }
}
