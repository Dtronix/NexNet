package com.dtronix.nexnet.pipes;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.fail;

import com.dtronix.nexnet.serialization.GoldenFiles;
import com.dtronix.nexnet.session.SessionProtocolException;
import java.util.Arrays;
import java.util.List;
import java.util.stream.Stream;
import org.junit.jupiter.api.DynamicTest;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.TestFactory;

/**
 * Replays the golden vectors of the pipe messages (K26) through {@link PipeWire}, the codec the pipe runtime reads
 * ({@code PipeManager}) and writes ({@code PipeImpl}) them with: {@code DuplexPipeUpdateState} bodies encode to the
 * golden bytes and decode to the golden fields, its reject vectors are refused with a protocol error, and the
 * {@code PipeWindowUpdate} frame's credit body round-trips. Framing is replayed by
 * {@code session.GoldenSessionVectorsTest}.
 */
class GoldenPipeVectorsTest {
    @TestFactory
    Stream<DynamicTest> replay() {
        return GoldenFiles.of(GoldenFiles.PIPE_TYPES).stream()
            .map(v -> DynamicTest.dynamicTest(v.toString(), () -> replay(v)));
    }

    private static void replay(GoldenFiles.Vector v) {
        if (!v.type.equals("DuplexPipeUpdateState")) {
            fail("Not a pipe vector type: " + v.type);
        }
        if (v.isReject()) {
            assertThrows(SessionProtocolException.class, () -> PipeWire.parseState(v.bytes()), "refused");
            return;
        }
        List<Object> value = v.list();
        int pipeId = GoldenFiles.int32(value.get(0));
        int state = GoldenFiles.int32(value.get(1));
        long window = GoldenFiles.integer(value.get(2));
        PipeWire.State decoded = PipeWire.parseState(v.bytes());
        assertEquals(pipeId, decoded.pipeId, "pipeId");
        assertEquals(state, decoded.state, "state");
        assertEquals(window, decoded.window, "window");
        if (v.mode.equals("both")) {
            assertEquals(GoldenFiles.hex(v.bytes()), GoldenFiles.hex(PipeWire.stateBody(pipeId, state, window)),
                "encode");
        }
    }

    @Test
    void pipeWindowUpdateCredit() {
        GoldenFiles.Vector v = GoldenFiles.byId("frame.pipe-window-update");
        long credit = GoldenFiles.integer(v.list().get(3));
        byte[] bytes = v.bytes();
        byte[] body = Arrays.copyOfRange(bytes, 5, bytes.length);
        assertArrayEquals(body, PipeWire.windowBody(credit), "encode");
        assertEquals(credit, PipeWire.parseWindow(body), "decode");
    }
}
