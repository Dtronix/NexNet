package com.dtronix.nexnet.logging;

import org.jspecify.annotations.Nullable;

/**
 * Receives the runtime's log messages. Set one with {@code ClientConfig.Builder.logger}; without one the runtime logs
 * nothing. Mirrors the C# {@code INexusLogger}: implementations must be thread-safe and must not block, since the
 * runtime may log from its reader and writer threads.
 */
@FunctionalInterface
public interface NexusLogger {
    /**
     * Writes a message.
     *
     * @param level the severity
     * @param category the category (for example the source of the message), or null
     * @param exception the related exception, or null
     * @param message the message
     */
    void log(NexusLogLevel level, @Nullable String category, @Nullable Throwable exception, String message);

    /**
     * Whether messages of this level are written. The runtime skips building messages that would be dropped.
     *
     * @param level the severity
     * @return true by default
     */
    default boolean isEnabled(NexusLogLevel level) {
        return level != NexusLogLevel.NONE;
    }

    /**
     * A logger for a part of the runtime, for example one client ({@code "CL1"}) or session. The default returns
     * this logger.
     *
     * @param pathSegment the segment added to the logging path, or null
     * @return the logger
     */
    default NexusLogger createLogger(@Nullable String pathSegment) {
        return this;
    }
}
