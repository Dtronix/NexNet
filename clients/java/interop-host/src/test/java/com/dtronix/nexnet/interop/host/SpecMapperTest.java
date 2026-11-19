package com.dtronix.nexnet.interop.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

import com.dtronix.nexnet.invocation.NexusRemoteError;
import java.util.HashMap;
import java.util.Map;
import org.junit.jupiter.api.Test;

class SpecMapperTest {
    private static Args hook(Object code, Object message) {
        Map<String, Object> map = new HashMap<>();
        if (code != null) {
            map.put("code", code);
        }
        if (message != null) {
            map.put("message", message);
        }
        return new Args(map);
    }

    @Test
    void noHookIsNoError() {
        assertNull(SpecMapper.invocationError(null));
    }

    @Test
    void hookWithCodeAndMessage() {
        NexusRemoteError error = SpecMapper.invocationError(hook(7L, "denied"));
        assertEquals(Integer.valueOf(7), error.code());
        assertEquals("denied", error.message());
    }

    @Test
    void hookWithOnlyAMessageHasANullCode() {
        NexusRemoteError error = SpecMapper.invocationError(hook(null, "message only"));
        assertNull(error.code());
        assertEquals("message only", error.message());
    }

    @Test
    void emptyHookHasNeither() {
        NexusRemoteError error = SpecMapper.invocationError(hook(null, null));
        assertNull(error.code());
        assertNull(error.message());
    }

    @Test
    void codeOutsideInt32IsABadRequest() {
        assertThrows(HostError.class, () -> SpecMapper.invocationError(hook(1L << 40, null)));
    }
}
