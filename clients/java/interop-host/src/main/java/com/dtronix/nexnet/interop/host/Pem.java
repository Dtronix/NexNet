package com.dtronix.nexnet.interop.host;

import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.KeyFactory;
import java.security.KeyStore;
import java.security.PrivateKey;
import java.security.cert.Certificate;
import java.security.cert.CertificateFactory;
import java.security.spec.PKCS8EncodedKeySpec;
import java.util.Base64;
import java.util.Collection;
import javax.net.ssl.KeyManager;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.X509KeyManager;

/** A client certificate from PEM text ({@code ClientSpec.clientCertificatePem} / {@code clientKeyPem}). */
final class Pem {
    private Pem() {
    }

    /**
     * A key manager presenting a PEM certificate chain and its PKCS#8 private key ({@code BEGIN PRIVATE KEY}).
     *
     * @param certificatePem the certificate (and chain)
     * @param keyPem the private key
     * @return the key manager
     * @throws GeneralSecurityException when either cannot be read
     */
    static X509KeyManager keyManager(String certificatePem, String keyPem) throws GeneralSecurityException {
        CertificateFactory factory = CertificateFactory.getInstance("X.509");
        Collection<? extends Certificate> chain = factory.generateCertificates(
            new ByteArrayInputStream(certificatePem.getBytes(StandardCharsets.US_ASCII)));
        if (chain.isEmpty()) {
            throw new GeneralSecurityException("No certificate in clientCertificatePem.");
        }
        PrivateKey key = privateKey(keyPem);
        char[] password = new char[0];
        KeyStore store = KeyStore.getInstance("PKCS12");
        try {
            store.load(null, null);
        } catch (java.io.IOException e) {
            throw new GeneralSecurityException(e);
        }
        store.setKeyEntry("client", key, password, chain.toArray(new Certificate[0]));
        KeyManagerFactory managers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        managers.init(store, password);
        for (KeyManager manager : managers.getKeyManagers()) {
            if (manager instanceof X509KeyManager) {
                return (X509KeyManager) manager;
            }
        }
        throw new GeneralSecurityException("No X.509 key manager.");
    }

    private static PrivateKey privateKey(String pem) throws GeneralSecurityException {
        String begin = "-----BEGIN PRIVATE KEY-----";
        String end = "-----END PRIVATE KEY-----";
        int start = pem.indexOf(begin);
        int stop = pem.indexOf(end);
        if (start < 0 || stop < start) {
            throw new GeneralSecurityException("clientKeyPem must be an unencrypted PKCS#8 key (BEGIN PRIVATE KEY).");
        }
        byte[] der = Base64.getMimeDecoder().decode(pem.substring(start + begin.length(), stop));
        PKCS8EncodedKeySpec spec = new PKCS8EncodedKeySpec(der);
        GeneralSecurityException last = null;
        for (String algorithm : new String[] {"RSA", "EC", "Ed25519", "DSA"}) {
            try {
                return KeyFactory.getInstance(algorithm).generatePrivate(spec);
            } catch (GeneralSecurityException e) {
                last = e;
            }
        }
        throw new GeneralSecurityException("Unsupported private key.", last);
    }
}
