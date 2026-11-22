package com.dtronix.nexnet.interop.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.invocation.NexusRemoteError;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.EnumSet;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.function.Supplier;
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

    private static Args script(Object... pairs) {
        Map<String, Object> map = new HashMap<>();
        for (int i = 0; i < pairs.length; i += 2) {
            map.put((String) pairs[i], pairs[i + 1]);
        }
        return new Args(map);
    }

    @Test
    void authenticateScriptNumbersItsCalls() throws Exception {
        Supplier<CompletableFuture<byte[]>> supplier =
            SpecMapper.authenticateScript(script("token", "t", "appendCallNumber", true, "delayMs", 1L));
        assertArrayEquals("t1".getBytes(StandardCharsets.UTF_8), supplier.get().get(5, TimeUnit.SECONDS));
        assertArrayEquals("t2".getBytes(StandardCharsets.UTF_8), supplier.get().get(5, TimeUnit.SECONDS));
    }

    @Test
    void authenticateScriptWithoutATokenReturnsNull() throws Exception {
        assertNull(SpecMapper.authenticateScript(script()).get().get(5, TimeUnit.SECONDS));
    }

    @Test
    void authenticateScriptThrows() {
        CompletableFuture<byte[]> future = SpecMapper.authenticateScript(script("throwMessage", "no")).get();
        ExecutionException e = assertThrows(ExecutionException.class, () -> future.get(5, TimeUnit.SECONDS));
        assertEquals("no", e.getCause().getMessage());
    }

    @Test
    void authenticateScriptHangs() throws Exception {
        CompletableFuture<byte[]> future = SpecMapper.authenticateScript(script("hang", true)).get();
        Thread.sleep(20);
        assertTrue(!future.isDone());
        future.cancel(true);
    }

    @Test
    void authenticateScriptRefusesUnknownMembers() {
        assertThrows(HostError.class, () -> SpecMapper.authenticateScript(script("other", 1L)));
    }

    @Test
    void metricsModesAreNativeOrMicrometer() {
        MetricsRecorder.validate(null);
        MetricsRecorder.validate("Native");
        MetricsRecorder.validate("Micrometer");
        assertThrows(HostError.class, () -> MetricsRecorder.validate("Other"));
    }

    @Test
    void reconnectReasonsByName() {
        assertEquals(EnumSet.of(DisconnectReason.SERVER_SHUTDOWN, DisconnectReason.SOCKET_ERROR),
            SpecMapper.reconnectReasons(Arrays.asList("ServerShutdown", "SocketError")));
        assertThrows(HostError.class, () -> SpecMapper.reconnectReasons(Arrays.asList("Nope")));
    }

    @Test
    void upgradeHeadersAreAddedToTheUpgradeRequest() {
        ClientConfig.Builder builder = ClientConfig.webSocket(URI.create("ws://localhost:1/ws"));
        SpecMapper.upgradeHeaders(builder, script("X-One", "1", "X-Two", "2"), "WebSocket");
        Map<String, String> headers = builder.build().upgradeHeaders();
        assertEquals("1", headers.get("X-One"));
        assertEquals("2", headers.get("X-Two"));
        assertEquals(2, headers.size());
    }

    @Test
    void upgradeHeadersNeedAnHttpTransport() {
        HostError error = assertThrows(HostError.class, () -> SpecMapper.upgradeHeaders(
            ClientConfig.tcp("localhost", 1), script("X-One", "1"), "Tcp"));
        assertEquals("NotSupported", error.kind());
    }

    @Test
    void reservedOrNonStringUpgradeHeadersAreBadRequests() {
        assertEquals("BadRequest", assertThrows(HostError.class, () -> SpecMapper.upgradeHeaders(
            ClientConfig.webSocket(URI.create("ws://localhost:1/ws")), script("Host", "x"), "WebSocket")).kind());
        assertEquals("BadRequest", assertThrows(HostError.class, () -> SpecMapper.upgradeHeaders(
            ClientConfig.webSocket(URI.create("ws://localhost:1/ws")), script("X-One", 1L), "WebSocket")).kind());
    }
}
