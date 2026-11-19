package com.dtronix.nexnet.transport;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.TlsOptions;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ConnectException;
import java.net.ServerSocket;
import java.nio.charset.StandardCharsets;
import java.security.cert.X509Certificate;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLException;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509KeyManager;
import javax.net.ssl.X509TrustManager;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(30)
class TcpTlsTransportTest {
    private static final String HOST = "127.0.0.1";

    static X509TrustManager testCa() throws Exception {
        try (InputStream in = TestServer.cert("test-ca.pem")) {
            return TlsTrust.fromCaCertificates(in, false);
        }
    }

    /** Echoes until the client closes; for TLS, completes the handshake first. */
    static void echo(java.net.Socket socket) throws Exception {
        if (socket instanceof SSLSocket) {
            ((SSLSocket) socket).startHandshake();
        }
        InputStream in = socket.getInputStream();
        OutputStream out = socket.getOutputStream();
        byte[] buffer = new byte[1024];
        int n;
        while ((n = in.read(buffer)) > 0) {
            out.write(buffer, 0, n);
            out.flush();
        }
    }

    /** Completes the handshake if the client lets it; failures are the point of the test. */
    static void handshakeOnly(java.net.Socket socket) {
        try {
            ((SSLSocket) socket).startHandshake();
            socket.getInputStream().read();
        } catch (Exception ignored) {
            // The client rejected the server.
        }
    }

    static void roundTrip(Transport transport) throws Exception {
        transport.output().write("hello".getBytes(StandardCharsets.US_ASCII));
        transport.output().flush();
        byte[] back = new byte[5];
        TestServer.readFully(transport.input(), back);
        assertEquals("hello", new String(back, StandardCharsets.US_ASCII));
    }

    static void assertAuthenticationFailure(Throwable e) {
        boolean ok = e instanceof SSLException
            || (e instanceof NexusTransportException
                && ((NexusTransportException) e).error() == TransportError.AUTHENTICATION_ERROR);
        assertTrue(ok, () -> "Expected a TLS authentication failure, got " + e);
    }

    static Transport connectTls(int port, TlsOptions tls) throws Exception {
        return NexusConnector.defaults().connect(ClientConfig.tcpTls(HOST, port).tls(tls).build(), 10_000);
    }

    @Test
    void tcpConnectsAndCarriesBytes() throws Exception {
        try (TestServer server = TestServer.plain(TcpTlsTransportTest::echo)) {
            Transport t = NexusConnector.defaults().connect(ClientConfig.tcp(HOST, server.port()).build(), 10_000);
            try {
                assertFalse(t.isSecure());
                assertNull(t.remoteCertificate());
                assertEquals("tcp", t.name());
                assertEquals(HOST, t.remoteAddress());
                assertEquals(server.port(), t.remotePort());
                roundTrip(t);
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
            server.await();
        }
    }

    @Test
    void closeUnblocksARead() throws Exception {
        try (TestServer server = TestServer.plain(TcpTlsTransportTest::echo)) {
            Transport t = NexusConnector.defaults().connect(ClientConfig.tcp(HOST, server.port()).build(), 10_000);
            CompletableFuture<Integer> read = CompletableFuture.supplyAsync(() -> {
                try {
                    return t.input().read();
                } catch (java.io.IOException e) {
                    return -2;
                }
            });
            Thread.sleep(100);
            t.close(DisconnectReason.GRACEFUL);
            t.close(DisconnectReason.GRACEFUL);
            assertTrue(read.get(5, TimeUnit.SECONDS) < 0);
        }
    }

    @Test
    void tcpRefusedIsAConnectException() throws Exception {
        int port;
        try (ServerSocket s = new ServerSocket(0)) {
            port = s.getLocalPort();
        }
        assertThrows(ConnectException.class, () ->
            NexusConnector.defaults().connect(ClientConfig.tcp(HOST, port).build(), 5_000));
    }

    @Test
    void tlsWithTrustedCaVerifiesTheIpAddress() throws Exception {
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::echo)) {
            Transport t = connectTls(server.port(), TlsOptions.builder().trustManager(testCa()).build());
            try {
                assertTrue(t.isSecure());
                assertEquals("tcptls", t.name());
                X509Certificate certificate = t.remoteCertificate();
                assertNotNull(certificate);
                assertTrue(certificate.getSubjectX500Principal().getName().contains("CN=localhost"));
                roundTrip(t);
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
            server.await();
        }
    }

    @Test
    void tlsWithServerNameVerifiesTheDnsName() throws Exception {
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::echo)) {
            Transport t = connectTls(server.port(),
                TlsOptions.builder().trustManager(testCa()).serverName("localhost").build());
            try {
                roundTrip(t);
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    @Test
    void tlsHostnameMismatchIsRejected() throws Exception {
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::handshakeOnly)) {
            Exception e = assertThrows(Exception.class, () -> connectTls(server.port(),
                TlsOptions.builder().trustManager(testCa()).serverName("example.com").build()));
            assertAuthenticationFailure(e);
        }
    }

    @Test
    void explicitHostnameCheckMatchesTheSubjectAltNames() throws Exception {
        // The post-handshake check that backs up the endpoint identification algorithm (Android).
        X509Certificate server = TlsTrustTestSupport.certificate("server.pem");
        assertTrue(HostnameMatcher.matches("localhost", server));
        assertTrue(HostnameMatcher.matches("LOCALHOST.", server));
        assertTrue(HostnameMatcher.matches("127.0.0.1", server));
        assertTrue(HostnameMatcher.matches("[::1]", server));
        assertTrue(HostnameMatcher.matches("0:0:0:0:0:0:0:1", server));
        assertFalse(HostnameMatcher.matches("example.com", server));
        assertFalse(HostnameMatcher.matches("127.0.0.2", server));
        // No subjectAltName: the CA certificate; there is no common-name fallback.
        assertFalse(HostnameMatcher.matches("localhost", TlsTrustTestSupport.certificate("test-ca.pem")));
    }

    @Test
    void tlsHostnameVerificationCanBeTurnedOff() throws Exception {
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::echo)) {
            Transport t = connectTls(server.port(), TlsOptions.builder().trustManager(testCa())
                .serverName("example.com").hostnameVerification(false).build());
            try {
                roundTrip(t);
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    @Test
    void tlsUntrustedServerIsRejectedBySystemTrust() throws Exception {
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::handshakeOnly)) {
            Exception e = assertThrows(Exception.class, () -> connectTls(server.port(), TlsOptions.defaults()));
            assertAuthenticationFailure(e);
        }
    }

    @Test
    void tlsCustomCaPlusSystemTrustAcceptsTheTestCa() throws Exception {
        X509TrustManager both;
        try (InputStream in = TestServer.cert("test-ca.pem")) {
            both = TlsTrust.fromCaCertificates(in, true);
        }
        assertTrue(both.getAcceptedIssuers().length > 1);
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::echo)) {
            Transport t = connectTls(server.port(), TlsOptions.builder().trustManager(both).build());
            try {
                roundTrip(t);
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    @Test
    void spkiPinMatchConnects() throws Exception {
        byte[] pin = TlsTrust.spkiSha256(TlsTrustTestSupport.certificate("server.pem"));
        // The pin published in certs/README.md.
        assertArrayEquals(java.util.Base64.getDecoder().decode("MZEa+vKvxw+5EI8S4C6W811NN39NrGjkKDakkDBwHdE="), pin);
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::echo)) {
            Transport t = connectTls(server.port(),
                TlsOptions.builder().trustManager(testCa()).pinCertificateSha256(pin).build());
            try {
                roundTrip(t);
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    @Test
    void anyMatchingCertificatePinConnects() throws Exception {
        byte[] leaf = TlsTrust.certificateSha256(TlsTrustTestSupport.certificate("server.pem"));
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::echo)) {
            Transport t = connectTls(server.port(), TlsOptions.builder().trustManager(testCa())
                .pinCertificateSha256(new byte[32]).pinCertificateSha256(leaf).build());
            try {
                roundTrip(t);
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    @Test
    void pinMismatchIsRejected() throws Exception {
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::handshakeOnly)) {
            Exception e = assertThrows(Exception.class, () -> connectTls(server.port(),
                TlsOptions.builder().trustManager(testCa()).pinCertificateSha256(new byte[32]).build()));
            assertAuthenticationFailure(e);
        }
    }

    @Test
    void pinMismatchIsRejectedWithACustomSslContext() throws Exception {
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, new TrustManager[] {testCa()}, null);
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::handshakeOnly)) {
            NexusTransportException e = assertThrows(NexusTransportException.class, () -> connectTls(server.port(),
                TlsOptions.builder().sslContext(context).pinCertificateSha256(new byte[32]).build()));
            assertEquals(TransportError.AUTHENTICATION_ERROR, e.error());
        }
    }

    @Test
    void clientCertificateFromPkcs12IsPresented() throws Exception {
        X509KeyManager keys;
        try (InputStream in = TestServer.cert("client.pfx")) {
            keys = TlsTrust.keyManagerFromPkcs12(in, TestServer.PFX_PASSWORD);
        }
        CompletableFuture<String> peer = new CompletableFuture<>();
        try (TestServer server = TestServer.tls(true, socket -> {
            SSLSocket ssl = (SSLSocket) socket;
            ssl.startHandshake();
            peer.complete(((X509Certificate) ssl.getSession().getPeerCertificates()[0])
                .getSubjectX500Principal().getName());
            echo(socket);
        })) {
            Transport t = connectTls(server.port(),
                TlsOptions.builder().trustManager(testCa()).keyManager(keys).build());
            try {
                roundTrip(t);
                assertTrue(peer.get(5, TimeUnit.SECONDS).contains("CN=nexnet-test-client"));
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    @Test
    void hostnameMatcherWildcards() {
        assertTrue(HostnameMatcher.matchesDns("a.example.com", "*.example.com"));
        assertFalse(HostnameMatcher.matchesDns("a.b.example.com", "*.example.com"));
        assertFalse(HostnameMatcher.matchesDns("example.com", "*.example.com"));
        assertFalse(HostnameMatcher.matchesDns("a.com", "*.com"));
        assertFalse(HostnameMatcher.matchesDns("ab.example.com", "a*.example.com"));
        assertTrue(HostnameMatcher.matchesDns("example.com", "example.com"));
        assertTrue(HostnameMatcher.isIpLiteral("[::1]"));
        assertTrue(HostnameMatcher.isIpLiteral("10.0.0.1"));
        assertFalse(HostnameMatcher.isIpLiteral("localhost"));
    }
}
