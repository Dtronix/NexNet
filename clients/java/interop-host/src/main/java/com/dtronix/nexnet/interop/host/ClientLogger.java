package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;

/** Sends a client's log lines as {@code log} events (control protocol §7.4). */
final class ClientLogger implements NexusLogger {
    private final InteropHost host;
    private final String test;
    private final String handle;
    private final NexusLogLevel minimum;

    ClientLogger(InteropHost host, String test, String handle, NexusLogLevel minimum) {
        this.host = host;
        this.test = test;
        this.handle = handle;
        this.minimum = minimum;
    }

    @Override
    public boolean isEnabled(NexusLogLevel level) {
        return level != NexusLogLevel.NONE && minimum != NexusLogLevel.NONE && level.ordinal() >= minimum.ordinal();
    }

    @Override
    public void log(NexusLogLevel level, String category, Throwable exception, String message) {
        if (!isEnabled(level)) {
            return;
        }
        String text = exception == null ? message : message + " " + exception;
        host.event(test, handle, "log", Protocol.map("level", Names.pascal(level), "category", category,
            "message", text));
    }
}
