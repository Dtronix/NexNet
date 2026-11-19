package com.dtronix.nexnet.client;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.invocation.RemoteExceptionDetail;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.reconnect.DefaultReconnectionPolicy;
import com.dtronix.nexnet.reconnect.ReconnectionPolicy;
import java.net.URI;
import java.security.GeneralSecurityException;
import java.util.Arrays;
import java.util.Map;
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
    void upgradeHeadersAndQueryTokenAreKept() {
        ClientConfig config = ClientConfig.webSocket(URI.create("wss://h/p"))
            .header("X-One", "1")
            .authorization("Bearer a")
            .header("x-one", "2")
            .authorization("Bearer b")
            .queryToken("access_token", "t")
            .build();
        Map<String, String> headers = config.upgradeHeaders();
        assertEquals(Arrays.asList("x-one", "Authorization"), Arrays.asList(headers.keySet().toArray()));
        assertEquals("2", headers.get("x-one"));
        assertEquals("Bearer b", headers.get("Authorization"));
        assertEquals("access_token", config.queryTokenName());
        assertEquals("t", config.queryTokenValue());
        assertThrows(UnsupportedOperationException.class, () -> headers.put("X", "y"));

        ClientConfig plain = ClientConfig.httpSocket(URI.create("http://h/")).build();
        assertTrue(plain.upgradeHeaders().isEmpty());
        assertNull(plain.queryTokenName());
        assertNull(plain.queryTokenValue());
    }

    @Test
    void upgradeHeadersAreValidatedLikeTheConnector() {
        ClientConfig.Builder b = ClientConfig.httpSocket(URI.create("https://h/"));
        assertThrows(IllegalArgumentException.class, () -> b.header("X-A", "a\r\nB: c"));
        assertThrows(IllegalArgumentException.class, () -> b.header("X-A", "a\nb"));
        assertThrows(IllegalArgumentException.class, () -> b.authorization("Bearer \r"));
        assertThrows(IllegalArgumentException.class, () -> b.header("Bad Name", "x"));
        assertThrows(IllegalArgumentException.class, () -> b.header("", "x"));
        assertThrows(IllegalArgumentException.class, () -> b.header("Host", "x"));
        assertThrows(IllegalArgumentException.class, () -> b.header("upgrade", "x"));
        assertThrows(IllegalArgumentException.class, () -> b.header("Connection", "x"));
        assertThrows(IllegalArgumentException.class, () -> b.header("Sec-WebSocket-Protocol", "x"));
        assertThrows(IllegalArgumentException.class, () -> b.header("Content-Length", "1"));
        assertThrows(IllegalArgumentException.class, () -> b.queryToken("", "x"));
        assertTrue(b.build().upgradeHeaders().isEmpty());
        assertNull(b.build().queryTokenName());
    }

    @Test
    void upgradeOptionsNeedAnUpgradeTransport() {
        assertThrows(IllegalStateException.class, () -> ClientConfig.tcp("h", 1).header("X-A", "1"));
        assertThrows(IllegalStateException.class, () -> ClientConfig.tcpTls("h", 1).authorization("Bearer x"));
        assertThrows(IllegalStateException.class, () -> ClientConfig.tcpTls("h", 1).queryToken("t", "x"));
    }

    @Test
    void tlsConvenienceOptionsRejectBadInput() {
        assertThrows(GeneralSecurityException.class,
            () -> TlsOptions.builder().trustCaCertificatesPem("not a certificate", false));
        assertThrows(GeneralSecurityException.class,
            () -> TlsOptions.builder().trustCaCertificates(new byte[0], false));
        assertThrows(Exception.class,
            () -> TlsOptions.builder().clientCertificatePkcs12(new byte[] {1, 2, 3}, "x".toCharArray()));
    }

    @Test
    void defaultsMatchCSharp() {
        ClientConfig config = ClientConfig.tcp("h", 1).build();
        assertEquals(50_000, config.connectionTimeoutMillis());
        assertEquals(15_000, config.handshakeTimeoutMillis());
        assertEquals(30_000, config.timeoutMillis());
        assertNull(config.pingIntervalMillis());
        assertTrue(config.tls().hostnameVerification());
        assertSame(TlsOptions.defaults(), config.tls());
        assertNull(config.logger());
    }

    @Test
    void pingIntervalIsNullableAndPositive() {
        assertEquals(5_000, ClientConfig.tcp("h", 1).pingIntervalMillis(5_000).build().pingIntervalMillis());
        assertNull(ClientConfig.tcp("h", 1).pingIntervalMillis(5_000).pingIntervalMillis(null).build()
            .pingIntervalMillis());
        assertThrows(IllegalArgumentException.class, () -> ClientConfig.tcp("h", 1).pingIntervalMillis(0));
        assertThrows(IllegalArgumentException.class, () -> ClientConfig.tcp("h", 1).pingIntervalMillis(-5));
    }

    @Test
    void invocationOptionsAndProgressTimeoutAreConfigurable() {
        ClientConfig defaults = ClientConfig.tcp("h", 1).build();
        assertSame(InvocationOptions.defaults(), defaults.invocationOptions());
        assertEquals(30_000, defaults.progressTimeoutMillis());

        InvocationOptions options = InvocationOptions.defaults().withMaxConcurrentInvocations(8)
            .withMaxSeparatePoolInvocations(3).withSessionBudget(1024 * 1024)
            .withRemoteExceptionDetail(RemoteExceptionDetail.TYPE_AND_MESSAGE);
        ClientConfig config = ClientConfig.tcp("h", 1).invocationOptions(options).progressTimeoutMillis(50).build();
        assertSame(options, config.invocationOptions());
        assertEquals(8, config.invocationOptions().maxConcurrentInvocations());
        assertEquals(3, config.invocationOptions().maxSeparatePoolInvocations());
        assertEquals(1024 * 1024, config.invocationOptions().sessionBudget());
        assertEquals(RemoteExceptionDetail.TYPE_AND_MESSAGE, config.invocationOptions().remoteExceptionDetail());
        assertEquals(50, config.progressTimeoutMillis());

        assertThrows(IllegalArgumentException.class, () -> ClientConfig.tcp("h", 1).progressTimeoutMillis(49));
        assertThrows(IllegalArgumentException.class, () -> ClientConfig.tcp("h", 1).progressTimeoutMillis(600_001));
        assertThrows(NullPointerException.class, () -> ClientConfig.tcp("h", 1).invocationOptions(null));
    }

    @Test
    void loggerIsConfigurable() {
        NexusLogger logger = (level, category, exception, message) -> { };
        assertSame(logger, ClientConfig.tcp("h", 1).logger(logger).build().logger());
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
