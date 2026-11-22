package com.dtronix.nexnet.transport;

import java.io.InputStream;
import java.security.KeyStore;
import java.security.cert.Certificate;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;

final class TlsTrustTestSupport {
    private TlsTrustTestSupport() {
    }

    static KeyStore storeOf(InputStream pem) throws Exception {
        KeyStore store = KeyStore.getInstance(KeyStore.getDefaultType());
        store.load(null, null);
        int i = 0;
        for (Certificate c : CertificateFactory.getInstance("X.509").generateCertificates(pem)) {
            store.setCertificateEntry("c" + i++, c);
        }
        return store;
    }

    static X509Certificate certificate(String name) throws Exception {
        try (InputStream in = TestServer.cert(name)) {
            return (X509Certificate) CertificateFactory.getInstance("X.509").generateCertificate(in);
        }
    }
}
