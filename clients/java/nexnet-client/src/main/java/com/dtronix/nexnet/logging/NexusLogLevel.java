package com.dtronix.nexnet.logging;

/**
 * The severity of a log message. Mirrors the C# {@code NexusLogLevel}.
 */
public enum NexusLogLevel {
    /** The most detailed messages; may contain sensitive data. */
    TRACE,
    /** Messages for interactive investigation during development. */
    DEBUG,
    /** The general flow of the client. */
    INFORMATION,
    /** An abnormal or unexpected event that does not stop the client. */
    WARNING,
    /** A failure of the current operation. */
    ERROR,
    /** An unrecoverable failure. */
    CRITICAL,
    /** Not a message level: a logger at this level writes nothing. */
    NONE
}
