package com.dtronix.nexnet.diagnostics;

/** What a large message carries. Mirrors the C# {@code NexusLargeMessageKind}. */
public enum NexusLargeMessageKind {
    /** Invocation arguments. */
    ARGUMENT,
    /** An invocation result. */
    RESULT
}
