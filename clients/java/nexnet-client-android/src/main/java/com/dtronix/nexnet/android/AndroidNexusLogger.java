package com.dtronix.nexnet.android;

import android.util.Log;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import org.jspecify.annotations.Nullable;

/**
 * A {@link NexusLogger} that writes to {@link Log} (logcat). Each message is prefixed with the logging path (for
 * example {@code CL1}) and category. Levels map to {@link Log#VERBOSE} (trace), {@link Log#DEBUG},
 * {@link Log#INFO}, {@link Log#WARN} and {@link Log#ERROR} (error and critical). Thread-safe.
 */
public final class AndroidNexusLogger implements NexusLogger {
    /** The default tag. */
    public static final String DEFAULT_TAG = "NexNet";

    private final String tag;
    private final NexusLogLevel minimumLevel;
    private final @Nullable String path;

    /** A logger with the tag {@value #DEFAULT_TAG} that writes {@code INFORMATION} and above. */
    public AndroidNexusLogger() {
        this(DEFAULT_TAG, NexusLogLevel.INFORMATION);
    }

    /**
     * Creates a logger.
     *
     * @param tag the logcat tag, at most 23 characters (the limit before API 26)
     * @param minimumLevel the lowest level written; {@link NexusLogLevel#NONE} writes nothing
     * @throws IllegalArgumentException for an empty tag or one longer than 23 characters
     */
    public AndroidNexusLogger(String tag, NexusLogLevel minimumLevel) {
        this(tag, minimumLevel, null);
    }

    private AndroidNexusLogger(String tag, NexusLogLevel minimumLevel, @Nullable String path) {
        if (tag == null || minimumLevel == null) {
            throw new NullPointerException(tag == null ? "tag" : "minimumLevel");
        }
        if (tag.isEmpty() || tag.length() > 23) {
            throw new IllegalArgumentException("The tag must be 1 to 23 characters: " + tag);
        }
        this.tag = tag;
        this.minimumLevel = minimumLevel;
        this.path = path;
    }

    /**
     * The logcat tag.
     *
     * @return the tag
     */
    public String tag() {
        return tag;
    }

    /**
     * The lowest level written.
     *
     * @return the level
     */
    public NexusLogLevel minimumLevel() {
        return minimumLevel;
    }

    @Override
    public boolean isEnabled(NexusLogLevel level) {
        return level != NexusLogLevel.NONE && minimumLevel != NexusLogLevel.NONE
            && level.compareTo(minimumLevel) >= 0;
    }

    @Override
    public NexusLogger createLogger(@Nullable String pathSegment) {
        if (pathSegment == null || pathSegment.isEmpty()) {
            return this;
        }
        return new AndroidNexusLogger(tag, minimumLevel, path == null ? pathSegment : path + "|" + pathSegment);
    }

    @Override
    public void log(NexusLogLevel level, @Nullable String category, @Nullable Throwable exception, String message) {
        if (!isEnabled(level)) {
            return;
        }
        StringBuilder text = new StringBuilder();
        if (path != null) {
            text.append(path).append(' ');
        }
        if (category != null && !category.isEmpty()) {
            text.append('[').append(category).append("] ");
        }
        if (level == NexusLogLevel.CRITICAL) {
            text.append("CRITICAL: ");
        }
        text.append(message);
        if (exception != null) {
            text.append('\n').append(Log.getStackTraceString(exception));
        }
        Log.println(priority(level), tag, text.toString());
    }

    /**
     * The {@link Log} priority of a level.
     *
     * @param level the level
     * @return the priority
     */
    static int priority(NexusLogLevel level) {
        switch (level) {
            case TRACE:
                return Log.VERBOSE;
            case DEBUG:
                return Log.DEBUG;
            case INFORMATION:
                return Log.INFO;
            case WARNING:
                return Log.WARN;
            default:
                return Log.ERROR;
        }
    }
}
