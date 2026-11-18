package com.dtronix.nexnet;

/**
 * A message is larger than the negotiated maximum message size.
 */
public class NexusMessageTooLargeException extends NexusException {
    private static final long serialVersionUID = 1L;

    private final long size;
    private final long limit;

    /**
     * Creates the exception.
     *
     * @param size the message size in bytes
     * @param limit the maximum message size in bytes
     */
    public NexusMessageTooLargeException(long size, long limit) {
        super("The message is " + size + " bytes; the limit is " + limit + " bytes.");
        this.size = size;
        this.limit = limit;
    }

    /**
     * The message size.
     *
     * @return the size in bytes
     */
    public long size() {
        return size;
    }

    /**
     * The maximum message size.
     *
     * @return the limit in bytes
     */
    public long limit() {
        return limit;
    }
}
