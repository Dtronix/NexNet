package com.dtronix.nexnet.serialization;

/**
 * The MessagePack type family of the next value, as returned by {@link MsgPackReader#peekType()}.
 */
public enum MsgPackType {
    /** nil. */
    NIL,
    /** true or false. */
    BOOLEAN,
    /** Any integer encoding. */
    INTEGER,
    /** float 32 or float 64. */
    FLOAT,
    /** str. */
    STRING,
    /** bin. */
    BINARY,
    /** array. */
    ARRAY,
    /** map. */
    MAP,
    /** ext (including the NexNet ext 78 primitive arrays). */
    EXTENSION
}
