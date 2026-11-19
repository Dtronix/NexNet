package com.dtronix.nexnet.pipes;

/**
 * The pipe limits of a client session (protocol specification section 9). Immutable; start from
 * {@link #defaults()}, which are the C# untrusted defaults ({@code MaxPipes} 32, {@code PipeWindow} 256 KiB,
 * progress timeout 30 s).
 */
public final class PipeOptions {
    /** The default {@link #maxPipes()}. */
    public static final int DEFAULT_MAX_PIPES = 32;
    /** The default {@link #pipeWindow()}: 256 KiB. */
    public static final int DEFAULT_PIPE_WINDOW = 256 * 1024;
    /** The default {@link #progressTimeoutMillis()}: 30 seconds. */
    public static final long DEFAULT_PROGRESS_TIMEOUT_MILLIS = 30_000;

    private static final PipeOptions DEFAULTS =
        new PipeOptions(DEFAULT_MAX_PIPES, DEFAULT_PIPE_WINDOW, DEFAULT_PROGRESS_TIMEOUT_MILLIS);

    private final int maxPipes;
    private final int pipeWindow;
    private final long progressTimeoutMillis;

    private PipeOptions(int maxPipes, int pipeWindow, long progressTimeoutMillis) {
        if (maxPipes < 1 || maxPipes > 255) {
            throw new IllegalArgumentException("maxPipes must be 1 to 255");
        }
        if (pipeWindow < 2 || pipeWindow > (1 << 30)) {
            throw new IllegalArgumentException("pipeWindow must be 2 bytes to 1 GiB");
        }
        if (progressTimeoutMillis < 1) {
            throw new IllegalArgumentException("progressTimeoutMillis must be positive");
        }
        this.maxPipes = maxPipes;
        this.pipeWindow = pipeWindow;
        this.progressTimeoutMillis = progressTimeoutMillis;
    }

    /**
     * The default options.
     *
     * @return the defaults
     */
    public static PipeOptions defaults() {
        return DEFAULTS;
    }

    /**
     * How many pipes may be open at once in the session, in both roles together.
     *
     * @return the count
     */
    public int maxPipes() {
        return maxPipes;
    }

    /**
     * The receive window of each pipe, reserved from the session budget while the pipe is open.
     *
     * @return bytes
     */
    public int pipeWindow() {
        return pipeWindow;
    }

    /**
     * How long a closing pipe, or a channel item given extra credit, may go without progress before the session
     * disconnects with {@code Stalled}.
     *
     * @return milliseconds
     */
    public long progressTimeoutMillis() {
        return progressTimeoutMillis;
    }

    /**
     * A copy with another {@link #maxPipes()}.
     *
     * @param value 1 to 255
     * @return the options
     */
    public PipeOptions withMaxPipes(int value) {
        return new PipeOptions(value, pipeWindow, progressTimeoutMillis);
    }

    /**
     * A copy with another {@link #pipeWindow()}.
     *
     * @param value bytes
     * @return the options
     */
    public PipeOptions withPipeWindow(int value) {
        return new PipeOptions(maxPipes, value, progressTimeoutMillis);
    }

    /**
     * A copy with another {@link #progressTimeoutMillis()}.
     *
     * @param value milliseconds
     * @return the options
     */
    public PipeOptions withProgressTimeoutMillis(long value) {
        return new PipeOptions(maxPipes, pipeWindow, value);
    }
}
