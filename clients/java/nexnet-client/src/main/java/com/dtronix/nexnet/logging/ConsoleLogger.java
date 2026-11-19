package com.dtronix.nexnet.logging;

import java.io.PrintStream;
import org.jspecify.annotations.Nullable;

/**
 * Writes log lines to standard output (or another stream), like the C# {@code ConsoleLogger}. For development: the
 * stream write may block. On Android, prefer an adapter to {@code android.util.Log} (see the nexnet-client-android
 * module).
 */
public final class ConsoleLogger extends CoreLogger {
    private final PrintStream out;

    /** Creates a root logger writing to {@link System#out}. */
    public ConsoleLogger() {
        this(System.out, null);
    }

    /**
     * Creates a root logger.
     *
     * @param out the stream lines are written to
     * @param pathSegment the first path segment, or null
     */
    public ConsoleLogger(PrintStream out, @Nullable String pathSegment) {
        super(pathSegment);
        if (out == null) {
            throw new NullPointerException("out");
        }
        this.out = out;
    }

    private ConsoleLogger(ConsoleLogger parent, @Nullable String pathSegment) {
        super(parent, pathSegment);
        this.out = parent.out;
    }

    @Override
    public void log(NexusLogLevel level, @Nullable String category, @Nullable Throwable exception, String message) {
        String line = formatLine(level, category, exception, message);
        if (line != null) {
            out.println(line);
        }
    }

    @Override
    public NexusLogger createLogger(@Nullable String pathSegment) {
        return new ConsoleLogger(this, pathSegment);
    }
}
