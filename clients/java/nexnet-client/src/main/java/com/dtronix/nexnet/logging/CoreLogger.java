package com.dtronix.nexnet.logging;

import java.io.PrintWriter;
import java.io.StringWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import org.jspecify.annotations.Nullable;

/**
 * The base of the bundled loggers, like the C# {@code CoreLogger}: a tree of loggers that share the root's minimum
 * level, enabled flag, behaviors and start time, each adding a segment to the logging path. Lines have the C# format:
 *
 * <pre>[1.234567][INF] [CL1|S2(Client)] Message exception</pre>
 *
 * <p>Thread-safe. A segment can be renamed at any time ({@link #pathSegment(String)}); descendants pick the new name
 * up on their next line.
 */
public abstract class CoreLogger implements NexusLogger {
    /** State shared by a root logger and all its descendants. */
    private static final class Root {
        final long startNanos = System.nanoTime();
        volatile NexusLogLevel minLevel = NexusLogLevel.TRACE;
        volatile boolean enabled = true;
        volatile int behaviors = NexusLogBehaviors.DEFAULT;
    }

    private final Root root;
    private final @Nullable CoreLogger parent;
    private volatile @Nullable String segment;

    /**
     * Creates a root logger.
     *
     * @param pathSegment the first path segment, or null
     */
    protected CoreLogger(@Nullable String pathSegment) {
        this.root = new Root();
        this.parent = null;
        this.segment = pathSegment;
    }

    /**
     * Creates a child logger that shares the parent's level, flags and clock.
     *
     * @param parent the parent logger
     * @param pathSegment the segment added to the parent's path, or null
     */
    protected CoreLogger(CoreLogger parent, @Nullable String pathSegment) {
        if (parent == null) {
            throw new NullPointerException("parent");
        }
        this.root = parent.root;
        this.parent = parent;
        this.segment = pathSegment;
    }

    /**
     * The minimum level written, shared by the whole tree. Default {@link NexusLogLevel#TRACE}.
     *
     * @return the level
     */
    public NexusLogLevel minLogLevel() {
        return root.minLevel;
    }

    /**
     * Sets the minimum level written, for the whole tree.
     *
     * @param level the level; {@link NexusLogLevel#NONE} writes nothing
     */
    public void minLogLevel(NexusLogLevel level) {
        if (level == null) {
            throw new NullPointerException("level");
        }
        root.minLevel = level;
    }

    /**
     * Whether logging is on, for the whole tree. Default true.
     *
     * @return true when on
     */
    public boolean logEnabled() {
        return root.enabled;
    }

    /**
     * Turns logging on or off for the whole tree.
     *
     * @param enabled true to log
     */
    public void logEnabled(boolean enabled) {
        root.enabled = enabled;
    }

    /**
     * The behavior flags ({@link NexusLogBehaviors}), shared by the whole tree.
     *
     * @return the flags
     */
    public int behaviors() {
        return root.behaviors;
    }

    /**
     * Sets the behavior flags for the whole tree.
     *
     * @param behaviors the flags
     */
    public void behaviors(int behaviors) {
        root.behaviors = behaviors;
    }

    /**
     * This logger's path segment.
     *
     * @return the segment, or null
     */
    public @Nullable String pathSegment() {
        return segment;
    }

    /**
     * Renames this logger's path segment; descendants use the new name from their next line on.
     *
     * @param pathSegment the segment, or null
     */
    public void pathSegment(@Nullable String pathSegment) {
        this.segment = pathSegment;
    }

    /**
     * The path from the root to this logger: the non-blank segments joined with {@code |}.
     *
     * @return the path, empty when no segment is set
     */
    public String formattedPath() {
        List<String> segments = new ArrayList<>();
        for (CoreLogger node = this; node != null; node = node.parent) {
            String s = node.segment;
            if (s != null && !s.trim().isEmpty()) {
                segments.add(s);
            }
        }
        StringBuilder sb = new StringBuilder();
        for (int i = segments.size() - 1; i >= 0; i--) {
            sb.append(segments.get(i));
            if (i > 0) {
                sb.append('|');
            }
        }
        return sb.toString();
    }

    @Override
    public boolean isEnabled(NexusLogLevel level) {
        return root.enabled && level != NexusLogLevel.NONE && level.compareTo(root.minLevel) >= 0;
    }

    /**
     * Formats a line, or returns null when it is not written (logging off or the level below the minimum).
     *
     * @param level the severity
     * @param category the category, or null
     * @param exception the exception, or null
     * @param message the message
     * @return the line, or null
     */
    protected @Nullable String formatLine(NexusLogLevel level, @Nullable String category,
                                          @Nullable Throwable exception, String message) {
        if (!root.enabled || level.compareTo(root.minLevel) < 0) {
            return null;
        }
        double seconds = (System.nanoTime() - root.startNanos) / 1_000_000_000.0;
        StringBuilder sb = new StringBuilder(128);
        sb.append('[').append(String.format(Locale.ROOT, "%.6f", seconds)).append("][")
            .append(shortName(level)).append("] [").append(formattedPath());
        if (category != null) {
            sb.append('(').append(category).append(')');
        }
        sb.append("] ").append(message);
        if (exception != null) {
            StringWriter trace = new StringWriter();
            exception.printStackTrace(new PrintWriter(trace));
            sb.append(' ').append(trace.toString().trim());
        }
        return sb.toString();
    }

    /**
     * The three-letter level name of the C# format.
     *
     * @param level the level
     * @return TRA, DEB, INF, WAR, ERR, CRI or NON
     */
    public static String shortName(NexusLogLevel level) {
        switch (level) {
            case TRACE:
                return "TRA";
            case DEBUG:
                return "DEB";
            case INFORMATION:
                return "INF";
            case WARNING:
                return "WAR";
            case ERROR:
                return "ERR";
            case CRITICAL:
                return "CRI";
            default:
                return "NON";
        }
    }
}
