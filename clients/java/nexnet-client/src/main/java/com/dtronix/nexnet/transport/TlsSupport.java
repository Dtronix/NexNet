package com.dtronix.nexnet.transport;

import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.client.TlsOptions;
import java.io.IOException;
import java.net.Socket;
import java.security.GeneralSecurityException;
import java.security.cert.Certificate;
import java.security.cert.X509Certificate;
import java.util.Collections;
import java.util.List;
import javax.net.ssl.KeyManager;
import javax.net.ssl.SNIHostName;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLParameters;
import javax.net.ssl.SSLSession;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509KeyManager;
import javax.net.ssl.X509TrustManager;

/**
 * Layers TLS over a connected socket: trust (platform, custom or {@code SSLContext}), optional pins and client
 * certificate, SNI, and host name verification that is always on unless the options turn it off. No ALPN
 * ({@code SSLParameters.setApplicationProtocols} is API 29); HTTP/1.1 is implied.
 */
final class TlsSupport {
    private TlsSupport() {
    }

    /**
     * Wraps the socket and completes the handshake within the socket's current read timeout.
     *
     * @param plain the connected socket; closed with the TLS socket
     * @param host the host being connected to
     * @param port the port
     * @param options the TLS options
     * @return the connected TLS socket
     */
    static SSLSocket handshake(Socket plain, String host, int port, TlsOptions options) throws IOException {
        SSLContext context = context(options);
        String configured = options.serverName();
        String name = HostnameMatcher.stripBrackets(configured != null ? configured : host);
        SSLSocket socket = (SSLSocket) context.getSocketFactory().createSocket(plain, name, port, true);
        try {
            socket.setUseClientMode(true);
            SSLParameters parameters = socket.getSSLParameters();
            if (options.hostnameVerification()) {
                parameters.setEndpointIdentificationAlgorithm("HTTPS");
            }
            if (!HostnameMatcher.isIpLiteral(name)) {
                parameters.setServerNames(Collections.singletonList(new SNIHostName(name)));
            }
            socket.setSSLParameters(parameters);
            socket.startHandshake();
            verify(socket.getSession(), name, options);
            return socket;
        } catch (IOException | RuntimeException e) {
            closeQuietly(socket);
            throw e;
        }
    }

    /** The checks that do not depend on the platform honoring the endpoint identification algorithm. */
    private static void verify(SSLSession session, String name, TlsOptions options) throws IOException {
        Certificate[] chain = session.getPeerCertificates();
        if (chain.length == 0 || !(chain[0] instanceof X509Certificate)) {
            throw new NexusTransportException(TransportError.AUTHENTICATION_ERROR,
                "The server presented no X.509 certificate.", null);
        }
        if (options.hostnameVerification() && !HostnameMatcher.matches(name, (X509Certificate) chain[0])) {
            throw new NexusTransportException(TransportError.AUTHENTICATION_ERROR,
                "The server certificate does not match the host name " + name + ".", null);
        }
        if (!TlsTrust.pinsMatch(chain, options.pinnedCertificateSha256())) {
            throw new NexusTransportException(TransportError.AUTHENTICATION_ERROR,
                "The server certificate chain matches no pinned certificate.", null);
        }
    }

    static X509Certificate leafCertificate(SSLSocket socket) {
        try {
            Certificate[] chain = socket.getSession().getPeerCertificates();
            return (X509Certificate) chain[0];
        } catch (IOException e) {
            throw new IllegalStateException("The handshake verified a certificate that is now missing.", e);
        }
    }

    private static SSLContext context(TlsOptions options) throws IOException {
        SSLContext provided = options.sslContext();
        if (provided != null) {
            return provided;
        }
        try {
            X509TrustManager configured = options.trustManager();
            X509TrustManager trust = configured != null ? configured : TlsTrust.systemTrustManager();
            List<byte[]> pins = options.pinnedCertificateSha256();
            TrustManager effective = pins.isEmpty() ? trust : new TrustManagers.Pinning(trust, pins);
            X509KeyManager key = options.keyManager();
            SSLContext context = SSLContext.getInstance("TLS");
            context.init(key == null ? null : new KeyManager[] {key}, new TrustManager[] {effective}, null);
            return context;
        } catch (GeneralSecurityException e) {
            throw new NexusTransportException(TransportError.INTERNAL_ERROR,
                "Could not create the TLS context: " + e.getMessage(), e);
        }
    }

    static void closeQuietly(Socket socket) {
        try {
            socket.close();
        } catch (IOException ignored) {
            // Already failing.
        }
    }
}
