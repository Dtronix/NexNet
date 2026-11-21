package com.dtronix.nexnet.logging;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import org.jspecify.annotations.Nullable;

/**
 * Keeps the last lines in memory, like the C# {@code RollingLogger}: a ring buffer shared by the root and all its
 * descendants. {@link #flush(Appendable)} writes them out (with a truncation notice when older lines were dropped)
 * and empties the buffer. Useful to attach a client's recent log to a failure report.
 */
public final class RollingLogger extends CoreLogger {
    /** The buffer shared by a tree. */
    private static final class Buffer {
        final String[] lines;
        int next;
        int total;

        Buffer(int capacity) {
            lines = new String[capacity];
        }
    }

    private final Buffer buffer;

    /** Creates a root logger keeping the last 200 lines. */
    public RollingLogger() {
        this(200, null);
    }

    /**
     * Creates a root logger.
     *
     * @param maxLines the number of lines kept, at least 1
     * @param pathSegment the first path segment, or null
     */
    public RollingLogger(int maxLines, @Nullable String pathSegment) {
        super(pathSegment);
        if (maxLines < 1) {
            throw new IllegalArgumentException("maxLines must be at least 1.");
        }
        this.buffer = new Buffer(maxLines);
    }

    private RollingLogger(RollingLogger parent, @Nullable String pathSegment) {
        super(parent, pathSegment);
        this.buffer = parent.buffer;
    }

    @Override
    public void log(NexusLogLevel level, @Nullable String category, @Nullable Throwable exception, String message) {
        String line = formatLine(level, category, exception, message);
        if (line == null) {
            return;
        }
        synchronized (buffer) {
            buffer.lines[buffer.next] = line;
            buffer.next = (buffer.next + 1) % buffer.lines.length;
            buffer.total++;
        }
    }

    @Override
    public NexusLogger createLogger(@Nullable String pathSegment) {
        return new RollingLogger(this, pathSegment);
    }

    /**
     * Lines written since the last flush, including those the buffer dropped.
     *
     * @return the count
     */
    public int totalLinesWritten() {
        synchronized (buffer) {
            return buffer.total;
        }
    }

    /**
     * The lines kept, oldest first, without emptying the buffer.
     *
     * @return a copy of the lines
     */
    public List<String> lines() {
        synchronized (buffer) {
            int capacity = buffer.lines.length;
            int count = Math.min(buffer.total, capacity);
            int start = buffer.total >= capacity ? buffer.next : 0;
            List<String> copy = new ArrayList<>(count);
            for (int i = 0; i < count; i++) {
                copy.add(buffer.lines[(start + i) % capacity]);
            }
            return copy;
        }
    }

    /**
     * Writes the kept lines, oldest first, each followed by a line feed, then empties the buffer. When older lines
     * were dropped, a first line says how many were written in total.
     *
     * @param out the destination
     * @throws UncheckedIOException when writing fails
     */
    public void flush(Appendable out) {
        synchronized (buffer) {
            if (buffer.total == 0) {
                return;
            }
            int capacity = buffer.lines.length;
            try {
                if (buffer.total > capacity) {
                    out.append("Truncating Log. Showing only last ").append(Integer.toString(capacity))
                        .append(" out of ").append(Integer.toString(buffer.total)).append(" total lines written.\n");
                }
                for (String line : lines()) {
                    out.append(line).append('\n');
                }
            } catch (IOException e) {
                throw new UncheckedIOException(e);
            }
            Arrays.fill(buffer.lines, null);
            buffer.next = 0;
            buffer.total = 0;
        }
    }
}
