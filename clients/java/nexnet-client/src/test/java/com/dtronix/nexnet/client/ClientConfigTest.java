package com.dtronix.nexnet.client;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.reconnect.DefaultReconnectionPolicy;
import com.dtronix.nexnet.reconnect.ReconnectionPolicy;
import java.net.URI;
import java.util.concurrent.CompletableFuture;
import org.junit.jupiter.api.Test;

class ClientConfigTest {
    @Test
    void isSecureFollowsTheTransport() {
        assertTrue(ClientConfig.tcpTls("localhost", 1).build().isSecureTransport());
        assertFalse(ClientConfig.tcp("localhost", 1).build().isSecureTransport());
        assertTrue(ClientConfig.webSocket(URI.create("WSS://h/p")).build().isSecureTransport());
        assertFalse(ClientConfig.webSocket(URI.create("ws://h/p")).build().isSecureTransport());
        assertTrue(ClientConfig.httpSocket(URI.create("https://h/p")).build().isSecureTransport());
        assertFalse(ClientConfig.httpSocket(URI.create("http://h/p")).build().isSecureTransport());
    }

    @Test
    void transportFactoriesValidate() {
        assertThrows(IllegalArgumentException.class, () -> ClientConfig.webSocket(URI.create("http://h/")));
        assertThrows(IllegalArgumentException.class, () -> ClientConfig.httpSocket(URI.create("wss://h/")));
        assertThrows(IllegalArgumentException.class, () -> ClientConfig.tcp("h", 0));
        assertThrows(IllegalArgumentException.class, () -> ClientConfig.tcpTls("h", 65536));
        assertThrows(IllegalArgumentException.class, () -> ClientConfig.tcp("", 1));
    }

    @Test
    void authenticateSuppliersAreMutuallyExclusive() {
        ClientConfig.Builder builder = ClientConfig.tcp("h", 1)
            .authenticate(() -> new byte[] {1})
            .authenticateAsync(() -> CompletableFuture.completedFuture(new byte[] {2}));
        assertThrows(IllegalStateException.class, builder::build);
    }

    @Test
    void defaultsMatchCSharp() {
        ClientConfig config = ClientConfig.tcp("h", 1).build();
        assertEquals(50_000, config.connectionTimeoutMillis());
        assertEquals(15_000, config.handshakeTimeoutMillis());
        assertEquals(30_000, config.timeoutMillis());
        assertEquals(10_000, config.pingIntervalMillis());
        assertTrue(config.tls().hostnameVerification());
        assertSame(TlsOptions.defaults(), config.tls());
    }

    @Test
    void defaultReconnectionPolicyDelays() {
        ReconnectionPolicy policy = new DefaultReconnectionPolicy();
        assertEquals(0, policy.reconnectDelayMillis(0));
        assertEquals(2_000, policy.reconnectDelayMillis(1));
        assertEquals(10_000, policy.reconnectDelayMillis(2));
        assertEquals(30_000, policy.reconnectDelayMillis(3));
        assertEquals(30_000, policy.reconnectDelayMillis(50));
        assertEquals(ReconnectionPolicy.STOP, new DefaultReconnectionPolicy(new long[] {1}, false).reconnectDelayMillis(1));
        assertTrue(policy.shouldReconnect(com.dtronix.nexnet.DisconnectReason.SOCKET_ERROR));
        assertFalse(policy.shouldReconnect(com.dtronix.nexnet.DisconnectReason.GRACEFUL));
    }
}
