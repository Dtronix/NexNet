package com.dtronix.nexnet.logging;

/**
 * Logging behavior flags, combined with {@code |}. Mirrors the C# {@code NexusLogBehaviors} flags enum; the values
 * are the same bits.
 */
public final class NexusLogBehaviors {
    /** Default behavior. */
    public static final int DEFAULT = 0;
    /** Logs the proxy's invocations of the server at {@link NexusLogLevel#INFORMATION} instead of DEBUG. */
    public static final int PROXY_INVOCATIONS_LOG_AS_INFO = 1;
    /** Logs the client nexus's invocations at {@link NexusLogLevel#INFORMATION} instead of DEBUG. */
    public static final int LOCAL_INVOCATIONS_LOG_AS_INFO = 1 << 1;
    /** Logs all incoming and outgoing data. For debugging only. */
    public static final int LOG_TRANSPORT_DATA = 1 << 2;

    private NexusLogBehaviors() {
    }

    /**
     * Whether a flag is set.
     *
     * @param behaviors the flags
     * @param flag the flag to test
     * @return true when every bit of {@code flag} is set in {@code behaviors}
     */
    public static boolean has(int behaviors, int flag) {
        return (behaviors & flag) == flag;
    }
}
