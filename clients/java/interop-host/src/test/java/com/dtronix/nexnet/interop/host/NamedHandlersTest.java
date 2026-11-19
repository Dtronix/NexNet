package com.dtronix.nexnet.interop.host;

import static org.junit.jupiter.api.Assertions.assertEquals;

import com.dtronix.nexnet.testnexus.scaffolding.TypedJson;
import org.junit.jupiter.api.Test;

class NamedHandlersTest {
    @Test
    void pumpItemsMatchTheCSharpReference() {
        assertEquals(-1L, NamedHandlers.pumpItem(TypedJson.prim(TypedJson.Prim.SBYTE), 255, 0));
        assertEquals(1L, NamedHandlers.pumpItem(TypedJson.prim(TypedJson.Prim.BYTE), 257, 0));
        assertEquals(7L, NamedHandlers.pumpItem(TypedJson.prim(TypedJson.Prim.INT64), 7, 0));
        assertEquals("cdef", NamedHandlers.pumpItem(TypedJson.prim(TypedJson.Prim.STRING), 2, 4));
        assertEquals("zab", NamedHandlers.pumpItem(TypedJson.prim(TypedJson.Prim.STRING), 25, 3));
        // byte[]: the pipe pattern of seed i, byte j = (j * 31 + i) mod 256.
        assertEquals(TypedJson.base64(new byte[] {3, 34, 65}), NamedHandlers.pumpItem(TypedJson.bin(), 3, 3));
    }

    @Test
    void pipeHashIsLowerCaseHex() {
        assertEquals("00ff10", PipeEntry.hex(new byte[] {0, (byte) 0xFF, 0x10}));
    }
}
