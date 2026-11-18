package com.dtronix.nexnet.client;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import javax.net.ssl.SSLContext;
import javax.net.ssl.X509KeyManager;
import javax.net.ssl.X509TrustManager;
import org.jspecify.annotations.Nullable;

/**
 * TLS settings for {@code tcpTls}, {@code wss://} and {@code https://} connections. Immutable; build one with
 * {@link #builder()}.
 *
 * <p>By default the platform trust store verifies the server and the host name is verified (Java's
 * {@code SSLSocket} does not verify host names on its own; the runtime sets the endpoint identification algorithm).
 */
public final class TlsOptions {
    private static final TlsOptions DEFAULTS = builder().build();

    private final @Nullable SSLContext sslContext;
    private final @Nullable X509TrustManager trustManager;
    private final @Nullable X509KeyManager keyManager;
    private final List<byte[]> pinnedCertificateSha256;
    private final boolean hostnameVerification;
    private final @Nullable String serverName;

    private TlsOptions(Builder builder) {
        this.sslContext = builder.sslContext;
        this.trustManager = builder.trustManager;
        this.keyManager = builder.keyManager;
        List<byte[]> pins = new ArrayList<>();
        for (byte[] pin : builder.pins) {
            pins.add(pin.clone());
        }
        this.pinnedCertificateSha256 = Collections.unmodifiableList(pins);
        this.hostnameVerification = builder.hostnameVerification;
        this.serverName = builder.serverName;
    }

    /**
     * The default options: platform trust, host name verification on.
     *
     * @return the defaults
     */
    public static TlsOptions defaults() {
        return DEFAULTS;
    }

    /**
     * Starts building options.
     *
     * @return a builder
     */
    public static Builder builder() {
        return new Builder();
    }

    /**
     * The SSL context to create sockets from.
     *
     * @return the context, or null to build one from the trust and key managers
     */
    public @Nullable SSLContext sslContext() {
        return sslContext;
    }

    /**
     * The trust manager that verifies the server certificate (for example, one trusting a private CA).
     *
     * @return the trust manager, or null for the platform default
     */
    public @Nullable X509TrustManager trustManager() {
        return trustManager;
    }

    /**
     * The key manager that supplies the client certificate.
     *
     * @return the key manager, or null for no client certificate
     */
    public @Nullable X509KeyManager keyManager() {
        return keyManager;
    }

    /**
     * SHA-256 hashes of the DER-encoded certificates the server chain must contain (pinning). Empty for no pinning.
     *
     * @return copies of the pins
     */
    public List<byte[]> pinnedCertificateSha256() {
        List<byte[]> copy = new ArrayList<>();
        for (byte[] pin : pinnedCertificateSha256) {
            copy.add(pin.clone());
        }
        return copy;
    }

    /**
     * Whether the server certificate must match the host name.
     *
     * @return true by default
     */
    public boolean hostnameVerification() {
        return hostnameVerification;
    }

    /**
     * The name used for SNI and host name verification.
     *
     * @return the name, or null to use the host being connected to
     */
    public @Nullable String serverName() {
        return serverName;
    }

    /** Builds {@link TlsOptions}. */
    public static final class Builder {
        private @Nullable SSLContext sslContext;
        private @Nullable X509TrustManager trustManager;
        private @Nullable X509KeyManager keyManager;
        private final List<byte[]> pins = new ArrayList<>();
        private boolean hostnameVerification = true;
        private @Nullable String serverName;

        private Builder() {
        }

        /**
         * Uses this SSL context. It takes precedence over {@link #trustManager} and {@link #keyManager}.
         *
         * @param sslContext the context
         * @return this builder
         */
        public Builder sslContext(@Nullable SSLContext sslContext) {
            this.sslContext = sslContext;
            return this;
        }

        /**
         * Verifies the server with this trust manager.
         *
         * @param trustManager the trust manager
         * @return this builder
         */
        public Builder trustManager(@Nullable X509TrustManager trustManager) {
            this.trustManager = trustManager;
            return this;
        }

        /**
         * Presents a client certificate from this key manager.
         *
         * @param keyManager the key manager
         * @return this builder
         */
        public Builder keyManager(@Nullable X509KeyManager keyManager) {
            this.keyManager = keyManager;
            return this;
        }

        /**
         * Adds a certificate pin: the SHA-256 hash of a DER-encoded certificate the server chain must contain.
         *
         * @param sha256 the 32-byte hash
         * @return this builder
         */
        public Builder pinCertificateSha256(byte[] sha256) {
            if (sha256.length != 32) {
                throw new IllegalArgumentException("A SHA-256 pin is 32 bytes.");
            }
            pins.add(sha256.clone());
            return this;
        }

        /**
         * Turns host name verification on or off. Turning it off accepts any certificate the trust manager accepts,
         * for any host; use it only in tests.
         *
         * @param enabled true to verify (the default)
         * @return this builder
         */
        public Builder hostnameVerification(boolean enabled) {
            this.hostnameVerification = enabled;
            return this;
        }

        /**
         * Uses this name for SNI and host name verification instead of the host being connected to.
         *
         * @param serverName the name
         * @return this builder
         */
        public Builder serverName(@Nullable String serverName) {
            this.serverName = serverName;
            return this;
        }

        /**
         * Builds the options.
         *
         * @return the options
         */
        public TlsOptions build() {
            return new TlsOptions(this);
        }
    }
}
