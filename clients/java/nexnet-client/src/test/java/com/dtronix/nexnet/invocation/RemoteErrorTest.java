package com.dtronix.nexnet.invocation;

import static com.dtronix.nexnet.invocation.Peer.array;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

import com.dtronix.nexnet.NexusRemoteInvocationException;
import org.junit.jupiter.api.Test;

class RemoteErrorTest {
    private static NexusRemoteInvocationException read(byte[] detail) {
        return InvocationRuntime.remoteError(detail, 0, detail.length);
    }

    @Test
    void readsAFullDetail() {
        NexusRemoteInvocationException e = read(array(-5, "T", "M"));
        assertEquals(-5, e.remoteErrorCode());
        assertEquals("T", e.remoteExceptionType());
        assertEquals("M", e.remoteMessage());
    }

    @Test
    void readsNilFields() {
        NexusRemoteInvocationException e = read(array(null, null, "only a message"));
        assertNull(e.remoteErrorCode());
        assertNull(e.remoteExceptionType());
        assertEquals("only a message", e.remoteMessage());
    }

    @Test
    void ignoresADetailOfAnotherShape() {
        assertNull(read(array(1, "T")).remoteExceptionType());
        assertNull(read(array("x", "T", "M")).remoteMessage());
        assertNull(read(array(1L << 33, "T", "M")).remoteMessage());
        assertNull(read(new byte[] {(byte) 0xA1, 0x78}).remoteMessage());
        assertNull(read(new byte[] {(byte) 0x93, 0x01}).remoteMessage());
    }

    @Test
    void ignoresStringsLongerThanTheLimits() {
        assertNull(read(array(1, "t".repeat(257), "M")).remoteErrorCode());
        assertNull(read(array(1, "T", "m".repeat(2049))).remoteErrorCode());
        assertEquals(2048, read(array(1, "T", "m".repeat(2048))).remoteMessage().length());
    }

    @Test
    void truncatesWithoutSplittingASurrogatePair() {
        assertEquals("abc", InvocationRuntime.truncate("abc", 5));
        assertEquals("ab", InvocationRuntime.truncate("abcd", 2));
        String pair = "a\uD83D\uDE00b";
        assertEquals("a", InvocationRuntime.truncate(pair, 2));
        assertEquals("a\uD83D\uDE00", InvocationRuntime.truncate(pair, 3));
        assertNull(InvocationRuntime.truncate(null, 3));
    }
}
