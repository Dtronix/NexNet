package com.dtronix.nexnet.interop.host;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.client.TlsOptions;
import com.dtronix.nexnet.okhttp.OkHttpWebSocketConnector;
import com.dtronix.nexnet.transport.NexusConnector;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import javax.net.ssl.X509TrustManager;
import okhttp3.OkHttpClient;
import org.junit.jupiter.api.Test;

class ConnectorsTest {
    private static final X509TrustManager TRUST_ALL = new X509TrustManager() {
        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType) {
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType) {
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return new X509Certificate[0];
        }
    };

    @Test
    void unsetOrBuiltinIsTheBuiltInConnector() {
        assertFalse(Connectors.parse(null));
        assertFalse(Connectors.parse(""));
        assertFalse(Connectors.parse("builtin"));
        assertTrue(Connectors.parse("okhttp"));
        assertTrue(Connectors.parse(" OkHttp "));
        assertThrows(IllegalArgumentException.class, () -> Connectors.parse("netty"));
    }

    @Test
    void builtInModeUsesTheDefaultConnectorForEveryTransport() {
        for (String transport : new String[] {"Tcp", "TcpTls", "HttpSocket", "WebSocket", "WebSocketTls"}) {
            assertSame(NexusConnector.defaults(), Connectors.select(transport, null, false));
        }
    }

    @Test
    void okHttpModeUsesOkHttpForWebSockets() {
        assertInstanceOf(OkHttpWebSocketConnector.class, Connectors.select("WebSocket", null, true));
        assertInstanceOf(OkHttpWebSocketConnector.class,
            Connectors.select("WebSocketTls", TlsOptions.defaults(), true));
    }

    @Test
    void okHttpModeRejectsOtherTransports() {
        assertThrows(HostError.class, () -> Connectors.select("Tcp", null, true));
        assertThrows(HostError.class, () -> Connectors.select("HttpSocket", null, true));
    }

    @Test
    void disabledHostnameVerificationAcceptsAnyName() {
        TlsOptions options = TlsOptions.builder().trustManager(TRUST_ALL).hostnameVerification(false).build();
        OkHttpClient client = Connectors.secure(options);
        assertTrue(client.hostnameVerifier().verify("anything.invalid", null));
        assertSame(TRUST_ALL, client.x509TrustManager());
    }

    @Test
    void pinsWrapTheTrustManagerAndRejectAChainWithoutAMatch() {
        TlsOptions options = TlsOptions.builder().trustManager(TRUST_ALL).pinCertificateSha256(new byte[32]).build();
        X509TrustManager trust = Connectors.secure(options).x509TrustManager();
        assertInstanceOf(Connectors.Pinning.class, trust);
        assertThrows(CertificateException.class, () -> trust.checkServerTrusted(new X509Certificate[0], "RSA"));
    }
}
