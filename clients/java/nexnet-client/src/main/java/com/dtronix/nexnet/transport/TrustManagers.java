package com.dtronix.nexnet.transport;

import java.net.Socket;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.List;
import javax.net.ssl.SSLEngine;
import javax.net.ssl.X509ExtendedTrustManager;
import javax.net.ssl.X509TrustManager;

/** Trust manager helpers: socket-aware delegation and the certificate pin check. */
final class TrustManagers {
    private TrustManagers() {
    }

    static void checkServer(X509TrustManager m, X509Certificate[] chain, String authType, Socket socket)
        throws CertificateException {
        if (m instanceof X509ExtendedTrustManager) {
            ((X509ExtendedTrustManager) m).checkServerTrusted(chain, authType, socket);
        } else {
            m.checkServerTrusted(chain, authType);
        }
    }

    static void checkClient(X509TrustManager m, X509Certificate[] chain, String authType, Socket socket)
        throws CertificateException {
        if (m instanceof X509ExtendedTrustManager) {
            ((X509ExtendedTrustManager) m).checkClientTrusted(chain, authType, socket);
        } else {
            m.checkClientTrusted(chain, authType);
        }
    }

    static void checkServer(X509TrustManager m, X509Certificate[] chain, String authType, SSLEngine engine)
        throws CertificateException {
        if (m instanceof X509ExtendedTrustManager) {
            ((X509ExtendedTrustManager) m).checkServerTrusted(chain, authType, engine);
        } else {
            m.checkServerTrusted(chain, authType);
        }
    }

    static void checkClient(X509TrustManager m, X509Certificate[] chain, String authType, SSLEngine engine)
        throws CertificateException {
        if (m instanceof X509ExtendedTrustManager) {
            ((X509ExtendedTrustManager) m).checkClientTrusted(chain, authType, engine);
        } else {
            m.checkClientTrusted(chain, authType);
        }
    }

    /**
     * Verifies the chain with a delegate, then requires a pin match. Pinning narrows trust: it never accepts a chain
     * the delegate rejects.
     */
    static final class Pinning extends X509ExtendedTrustManager {
        private final X509TrustManager delegate;
        private final List<byte[]> pins;

        Pinning(X509TrustManager delegate, List<byte[]> pins) {
            this.delegate = delegate;
            this.pins = pins;
        }

        private void pin(X509Certificate[] chain) throws CertificateException {
            if (!TlsTrust.pinsMatch(chain, pins)) {
                throw new CertificateException("The server certificate chain matches no pinned certificate.");
            }
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            delegate.checkClientTrusted(chain, authType);
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            delegate.checkServerTrusted(chain, authType);
            pin(chain);
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType, Socket socket)
            throws CertificateException {
            checkClient(delegate, chain, authType, socket);
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType, Socket socket)
            throws CertificateException {
            checkServer(delegate, chain, authType, socket);
            pin(chain);
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType, SSLEngine engine)
            throws CertificateException {
            checkClient(delegate, chain, authType, engine);
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType, SSLEngine engine)
            throws CertificateException {
            checkServer(delegate, chain, authType, engine);
            pin(chain);
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return delegate.getAcceptedIssuers();
        }
    }
}
