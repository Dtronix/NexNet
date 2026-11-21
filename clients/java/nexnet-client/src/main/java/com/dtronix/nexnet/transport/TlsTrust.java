package com.dtronix.nexnet.transport;

import java.io.IOException;
import java.io.InputStream;
import java.net.Socket;
import java.security.GeneralSecurityException;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.cert.Certificate;
import java.security.cert.CertificateException;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Collection;
import java.util.List;
import javax.net.ssl.KeyManager;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLEngine;
import javax.net.ssl.TrustManager;
import javax.net.ssl.TrustManagerFactory;
import javax.net.ssl.X509ExtendedTrustManager;
import javax.net.ssl.X509KeyManager;
import javax.net.ssl.X509TrustManager;
import org.jspecify.annotations.Nullable;

/**
 * Builds the trust and key managers that {@link com.dtronix.nexnet.client.TlsOptions} takes: a private CA from PEM
 * or a {@code KeyStore}, with or without the platform trust store, and a client certificate from PKCS#12. Also
 * computes certificate pins.
 *
 * <pre>{@code
 * TlsOptions tls = TlsOptions.builder()
 *     .trustManager(TlsTrust.fromCaCertificates(caPem, false))
 *     .keyManager(TlsTrust.keyManagerFromPkcs12(clientPfx, password))
 *     .build();
 * }</pre>
 *
 * <p>Pins ({@code TlsOptions.Builder.pinCertificateSha256}) match when they equal either the SHA-256 of a chain
 * certificate's DER encoding ({@link #certificateSha256}) or the SHA-256 of its DER SubjectPublicKeyInfo
 * ({@link #spkiSha256}, the form that survives certificate renewal with the same key).
 */
public final class TlsTrust {
    private static volatile @Nullable X509TrustManager system;

    private TlsTrust() {
    }

    /**
     * The platform's default trust manager.
     *
     * @return the trust manager
     * @throws GeneralSecurityException when the platform has none
     */
    public static X509TrustManager systemTrustManager() throws GeneralSecurityException {
        X509TrustManager current = system;
        if (current == null) {
            current = fromFactory(null);
            system = current;
        }
        return current;
    }

    /**
     * Trusts the CA certificates in a stream (PEM, one or more {@code BEGIN CERTIFICATE} blocks, or DER).
     *
     * @param certificates the certificates; read to the end, not closed
     * @param includeSystemTrust true to also trust the platform's CAs
     * @return the trust manager
     * @throws GeneralSecurityException when the certificates cannot be parsed or the stream holds none
     * @throws IOException when the stream fails
     */
    public static X509TrustManager fromCaCertificates(InputStream certificates, boolean includeSystemTrust)
        throws GeneralSecurityException, IOException {
        Collection<? extends Certificate> parsed = CertificateFactory.getInstance("X.509")
            .generateCertificates(certificates);
        if (parsed.isEmpty()) {
            throw new CertificateException("The stream holds no certificate.");
        }
        KeyStore store = KeyStore.getInstance(KeyStore.getDefaultType());
        store.load(null, null);
        int i = 0;
        for (Certificate certificate : parsed) {
            store.setCertificateEntry("ca" + i++, certificate);
        }
        return fromKeyStore(store, includeSystemTrust);
    }

    /**
     * Trusts the certificate entries of a key store.
     *
     * @param trustStore the key store
     * @param includeSystemTrust true to also trust the platform's CAs
     * @return the trust manager
     * @throws GeneralSecurityException when no trust manager can be built
     */
    public static X509TrustManager fromKeyStore(KeyStore trustStore, boolean includeSystemTrust)
        throws GeneralSecurityException {
        if (trustStore == null) {
            throw new NullPointerException("trustStore");
        }
        X509TrustManager custom = fromFactory(trustStore);
        return includeSystemTrust ? new EitherTrustManager(custom, systemTrustManager()) : custom;
    }

    /**
     * Loads a client certificate and its private key from a PKCS#12 ({@code .pfx}/{@code .p12}) stream.
     *
     * @param pkcs12 the PKCS#12 data; read to the end, not closed
     * @param password the store and key password
     * @return the key manager
     * @throws GeneralSecurityException when the store cannot be read
     * @throws IOException when the stream fails or the password is wrong
     */
    public static X509KeyManager keyManagerFromPkcs12(InputStream pkcs12, char[] password)
        throws GeneralSecurityException, IOException {
        KeyStore store = KeyStore.getInstance("PKCS12");
        store.load(pkcs12, password);
        return keyManagerFromKeyStore(store, password);
    }

    /**
     * Presents the key entries of a key store as client certificates.
     *
     * @param keyStore the key store
     * @param password the key password
     * @return the key manager
     * @throws GeneralSecurityException when no key manager can be built
     */
    public static X509KeyManager keyManagerFromKeyStore(KeyStore keyStore, char[] password)
        throws GeneralSecurityException {
        KeyManagerFactory factory = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        factory.init(keyStore, password);
        for (KeyManager manager : factory.getKeyManagers()) {
            if (manager instanceof X509KeyManager) {
                return (X509KeyManager) manager;
            }
        }
        throw new GeneralSecurityException("No X.509 key manager is available.");
    }

    /**
     * The SHA-256 of a certificate's DER encoding.
     *
     * @param certificate the certificate
     * @return the 32-byte hash
     * @throws CertificateException when the certificate cannot be encoded
     */
    public static byte[] certificateSha256(X509Certificate certificate) throws CertificateException {
        return sha256(certificate.getEncoded());
    }

    /**
     * The SHA-256 of a certificate's DER SubjectPublicKeyInfo (an SPKI pin; base64 of it is the HPKP form).
     *
     * @param certificate the certificate
     * @return the 32-byte hash
     */
    public static byte[] spkiSha256(X509Certificate certificate) {
        return sha256(certificate.getPublicKey().getEncoded());
    }

    static boolean pinsMatch(Certificate[] chain, List<byte[]> pins) {
        if (pins.isEmpty()) {
            return true;
        }
        for (Certificate c : chain) {
            if (!(c instanceof X509Certificate)) {
                continue;
            }
            X509Certificate certificate = (X509Certificate) c;
            List<byte[]> hashes = new ArrayList<>(2);
            hashes.add(spkiSha256(certificate));
            try {
                hashes.add(certificateSha256(certificate));
            } catch (CertificateException ignored) {
                // The SPKI hash still applies.
            }
            for (byte[] hash : hashes) {
                for (byte[] pin : pins) {
                    if (MessageDigest.isEqual(hash, pin)) {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static byte[] sha256(byte[] data) {
        try {
            return MessageDigest.getInstance("SHA-256").digest(data);
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException("SHA-256 is not available.", e);
        }
    }

    private static X509TrustManager fromFactory(@Nullable KeyStore store) throws GeneralSecurityException {
        TrustManagerFactory factory = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());
        factory.init(store);
        for (TrustManager manager : factory.getTrustManagers()) {
            if (manager instanceof X509TrustManager) {
                return (X509TrustManager) manager;
            }
        }
        throw new GeneralSecurityException("No X.509 trust manager is available.");
    }

    /** Accepts a chain either delegate accepts; the first delegate's failure is reported when both fail. */
    private static final class EitherTrustManager extends X509ExtendedTrustManager {
        private final X509TrustManager first;
        private final X509TrustManager second;

        EitherTrustManager(X509TrustManager first, X509TrustManager second) {
            this.first = first;
            this.second = second;
        }

        private interface Check {
            void run(X509TrustManager manager) throws CertificateException;
        }

        private void either(Check check) throws CertificateException {
            try {
                check.run(first);
            } catch (CertificateException e) {
                try {
                    check.run(second);
                } catch (CertificateException ignored) {
                    throw e;
                }
            }
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            either(m -> m.checkClientTrusted(chain, authType));
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            either(m -> m.checkServerTrusted(chain, authType));
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType, Socket socket)
            throws CertificateException {
            either(m -> TrustManagers.checkClient(m, chain, authType, socket));
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType, Socket socket)
            throws CertificateException {
            either(m -> TrustManagers.checkServer(m, chain, authType, socket));
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType, SSLEngine engine)
            throws CertificateException {
            either(m -> TrustManagers.checkClient(m, chain, authType, engine));
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType, SSLEngine engine)
            throws CertificateException {
            either(m -> TrustManagers.checkServer(m, chain, authType, engine));
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            X509Certificate[] a = first.getAcceptedIssuers();
            X509Certificate[] b = second.getAcceptedIssuers();
            X509Certificate[] all = new X509Certificate[a.length + b.length];
            System.arraycopy(a, 0, all, 0, a.length);
            System.arraycopy(b, 0, all, a.length, b.length);
            return all;
        }
    }
}
