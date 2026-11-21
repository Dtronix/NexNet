package com.dtronix.nexnet;

/**
 * The kind of a {@link NexusDateTime}, as in the C# {@code DateTimeKind}.
 */
public enum NexusDateTimeKind {
    /** Neither UTC nor local time. */
    UNSPECIFIED,
    /** Coordinated universal time. */
    UTC,
    /** Local time of the machine that produced the value. */
    LOCAL
}
