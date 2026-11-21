package com.dtronix.nexnet.pipes;

/**
 * The pipe limits of a client session (protocol specification section 9). Immutable; start from
 * {@link #defaults()}, which are the C# untrusted defaults ({@code MaxPipes} 32, {@code PipeWindow} 256 KiB). Set
 * them with {@code ClientConfig.Builder.pipeOptions}. How long a pipe may go without progress is the session's
 * progress timeout, {@code ClientConfig.Builder.progressTimeoutMillis}.
 */
public final class PipeOptions {
    /** The default {@link #maxPipes()}. */
    public static final int DEFAULT_MAX_PIPES = 32;
    /** The default {@link #pipeWindow()}: 256 KiB. */
    public static final int DEFAULT_PIPE_WINDOW = 256 * 1024;

    private static final PipeOptions DEFAULTS = new PipeOptions(DEFAULT_MAX_PIPES, DEFAULT_PIPE_WINDOW);

    private final int maxPipes;
    private final int pipeWindow;

    private PipeOptions(int maxPipes, int pipeWindow) {
        if (maxPipes < 1 || maxPipes > 255) {
            throw new IllegalArgumentException("maxPipes must be 1 to 255");
        }
        if (pipeWindow < 2 || pipeWindow > (1 << 30)) {
            throw new IllegalArgumentException("pipeWindow must be 2 bytes to 1 GiB");
        }
        this.maxPipes = maxPipes;
        this.pipeWindow = pipeWindow;
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
     * A copy with another {@link #maxPipes()}.
     *
     * @param value 1 to 255
     * @return the options
     * @throws IllegalArgumentException when out of range
     */
    public PipeOptions withMaxPipes(int value) {
        return new PipeOptions(value, pipeWindow);
    }

    /**
     * A copy with another {@link #pipeWindow()}.
     *
     * @param value bytes, 2 to 1 GiB
     * @return the options
     * @throws IllegalArgumentException when out of range
     */
    public PipeOptions withPipeWindow(int value) {
        return new PipeOptions(maxPipes, value);
    }

    @Override
    public String toString() {
        return "PipeOptions[maxPipes=" + maxPipes + ", pipeWindow=" + pipeWindow + "]";
    }
}
