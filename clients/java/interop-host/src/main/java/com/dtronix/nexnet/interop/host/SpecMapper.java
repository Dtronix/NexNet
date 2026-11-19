package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.TlsOptions;
import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.invocation.NexusRemoteError;
import com.dtronix.nexnet.invocation.RemoteExceptionDetail;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.reconnect.DefaultReconnectionPolicy;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.session.SessionTimers;
import java.net.URI;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.cert.X509Certificate;
import java.util.Arrays;
import java.util.Base64;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import javax.net.ssl.X509TrustManager;

/** Maps a {@code ClientSpec} (control protocol §6.4.1) to the Java client's configuration. */
final class SpecMapper {
    private static final Set<String> MEMBERS = new HashSet<>(Arrays.asList(
        "transport", "address", "tlsServerName", "caPem", "clientCertificatePem", "clientKeyPem", "pinSha256",
        "insecureSkipVerify", "connectionTimeoutMs", "timeoutMs", "handshakeTimeoutMs", "pingIntervalMs",
        "maxMessageSize", "invocationCredits", "maxConcurrentConnectionInvocations", "remoteExceptionDetail",
        "onInvocationException", "reconnectDelaysMs", "reconnectContinuously", "authToken", "authorizationHeader",
        "fakeTime", "logLevel"));

    private SpecMapper() {
    }

    /** What a spec configures. */
    static final class Setup {
        final ClientConfig config;
        final SessionOptions sessionOptions;
        final SessionTimers timers;
        final FakeTimers fakeTime;
        /** The error the client nexus's onInvocationException hook returns, or null for no hook. */
        final NexusRemoteError invocationError;

        Setup(ClientConfig config, SessionOptions sessionOptions, SessionTimers timers, FakeTimers fakeTime,
              NexusRemoteError invocationError) {
            this.config = config;
            this.sessionOptions = sessionOptions;
            this.timers = timers;
            this.fakeTime = fakeTime;
            this.invocationError = invocationError;
        }
    }

    /**
     * Maps a spec.
     *
     * @param spec the spec
     * @param logger the client logger, or null for no logging
     * @param fakeTime the clock to use when the spec asks for fake time (a pool shares one), or null for a new one
     * @return the configuration
     * @throws HostError NotSupported for an option the Java client cannot honor, BadRequest for a malformed spec
     */
    static Setup map(Args spec, NexusLogger logger, FakeTimers fakeTime) {
        for (String member : spec.raw().keySet()) {
            if (!MEMBERS.contains(member)) {
                throw HostError.notSupported("The JVM host does not know the ClientSpec member '" + member + "'.");
            }
        }
        NexusRemoteError invocationError = invocationError(spec.optObj("onInvocationException"));

        String transport = spec.str("transport");
        String address = spec.str("address");
        boolean secure;
        ClientConfig.Builder builder;
        switch (transport) {
            case "Tcp": {
                URI uri = hostPort(address, "tcp://");
                builder = ClientConfig.tcp(uri.getHost(), uri.getPort());
                secure = false;
                break;
            }
            case "TcpTls": {
                URI uri = hostPort(address, "tls://");
                builder = ClientConfig.tcpTls(uri.getHost(), uri.getPort());
                secure = true;
                break;
            }
            case "HttpSocket":
            case "HttpSocketTls":
                builder = ClientConfig.httpSocket(uri(address));
                secure = transport.endsWith("Tls");
                break;
            case "WebSocket":
            case "WebSocketTls":
                builder = ClientConfig.webSocket(uri(address));
                secure = transport.endsWith("Tls");
                break;
            default:
                throw HostError.notSupported("The Java client has no " + transport + " transport.");
        }

        if (secure) {
            builder.tls(tls(spec, transport.equals("TcpTls")));
        } else if (spec.has("caPem") || spec.has("pinSha256") || spec.has("clientCertificatePem")) {
            throw HostError.badRequest("TLS options need a TLS transport, not " + transport + ".");
        }

        String authorization = spec.optStr("authorizationHeader");
        if (authorization != null) {
            try {
                builder.authorization(authorization);
            } catch (IllegalStateException e) {
                throw HostError.notSupported("An Authorization header needs an HTTP transport, not " + transport + ".");
            }
        }
        String token = spec.optStr("authToken");
        if (token != null) {
            byte[] bytes = token.getBytes(StandardCharsets.UTF_8);
            builder.authenticate(() -> bytes.clone());
        }
        Long connectionTimeout = spec.optInteger("connectionTimeoutMs");
        if (connectionTimeout != null) {
            builder.connectionTimeoutMillis(connectionTimeout.intValue());
        }
        Long timeout = spec.optInteger("timeoutMs");
        if (timeout != null) {
            builder.timeoutMillis(timeout.intValue());
        }
        Long handshakeTimeout = spec.optInteger("handshakeTimeoutMs");
        if (handshakeTimeout != null) {
            builder.handshakeTimeoutMillis(handshakeTimeout.intValue());
        }
        Long pingInterval = spec.optInteger("pingIntervalMs");
        if (pingInterval != null) {
            builder.pingIntervalMillis(pingInterval.intValue());
        }

        InvocationOptions invocations = InvocationOptions.defaults();
        Long maxConcurrent = spec.optInteger("maxConcurrentConnectionInvocations");
        if (maxConcurrent != null) {
            invocations = invocations.withMaxConcurrentInvocations(maxConcurrent.intValue());
        }
        String detail = spec.optStr("remoteExceptionDetail");
        if (detail != null) {
            invocations = invocations.withRemoteExceptionDetail(Names.parse(RemoteExceptionDetail.class, detail));
        }
        builder.invocationOptions(invocations);

        if (spec.has("reconnectDelaysMs")) {
            List<Object> delays = spec.list("reconnectDelaysMs");
            long[] millis = new long[delays.size()];
            for (int i = 0; i < millis.length; i++) {
                millis[i] = Args.toLong("reconnectDelaysMs", delays.get(i));
            }
            builder.reconnectionPolicy(new DefaultReconnectionPolicy(millis, spec.bool("reconnectContinuously")));
        }
        if (logger != null) {
            builder.logger(logger);
        }

        SessionOptions options = SessionOptions.defaults();
        Long maxMessageSize = spec.optInteger("maxMessageSize");
        if (maxMessageSize != null) {
            options = options.withMaxMessageSize(maxMessageSize.intValue());
        }
        Long credits = spec.optInteger("invocationCredits");
        if (credits != null) {
            options = options.withInvocationCredits(credits.intValue());
        }

        FakeTimers fake = null;
        SessionTimers timers = SessionTimers.system();
        if (spec.bool("fakeTime")) {
            fake = fakeTime != null ? fakeTime : new FakeTimers();
            timers = fake;
        }
        return new Setup(builder.build(), options, timers, fake, invocationError);
    }

    /** The error of {@code onInvocationException} ({@code {code?, message?}}), or null when the spec has no hook. */
    static NexusRemoteError invocationError(Args hook) {
        if (hook == null) {
            return null;
        }
        for (String member : hook.raw().keySet()) {
            if (!member.equals("code") && !member.equals("message")) {
                throw HostError.notSupported("The JVM host does not know the onInvocationException member '" + member + "'.");
            }
        }
        Long code = hook.optInteger("code");
        if (code != null && (code < Integer.MIN_VALUE || code > Integer.MAX_VALUE)) {
            throw HostError.badRequest("The onInvocationException code " + code + " is not an Int32.");
        }
        return new NexusRemoteError(code == null ? null : code.intValue(), hook.optStr("message"));
    }

    private static URI uri(String address) {
        try {
            return new URI(address);
        } catch (URISyntaxException e) {
            throw HostError.badRequest("Malformed address '" + address + "'.");
        }
    }

    private static URI hostPort(String address, String scheme) {
        String rest = address.startsWith(scheme) ? address.substring(scheme.length()) : address;
        URI uri = uri("tcp://" + rest);
        if (uri.getHost() == null || uri.getPort() < 0) {
            throw HostError.badRequest("Malformed address '" + address + "'.");
        }
        return uri;
    }

    private static TlsOptions tls(Args spec, boolean tcp) {
        TlsOptions.Builder tls = TlsOptions.builder();
        String serverName = spec.optStr("tlsServerName");
        if (tcp) {
            // As the C# client: TLS over TCP verifies "localhost" unless the spec names the server.
            tls.serverName(serverName != null ? serverName : "localhost");
        }
        try {
            if (spec.bool("insecureSkipVerify")) {
                tls.trustManager(TRUST_ALL).hostnameVerification(false);
            } else if (spec.has("pinSha256")) {
                // The pin alone decides trust (ClientSpec.PinSha256).
                byte[] pin = Base64.getDecoder().decode(spec.str("pinSha256"));
                tls.trustManager(TRUST_ALL).hostnameVerification(false).pinCertificateSha256(pin);
            } else if (spec.has("caPem")) {
                tls.trustCaCertificatesPem(spec.str("caPem"), false);
            }
            if (spec.has("clientCertificatePem")) {
                String key = spec.optStr("clientKeyPem");
                if (key == null) {
                    throw HostError.badRequest("clientCertificatePem needs clientKeyPem.");
                }
                tls.keyManager(Pem.keyManager(spec.str("clientCertificatePem"), key));
            }
        } catch (GeneralSecurityException | IllegalArgumentException e) {
            throw HostError.badRequest("Bad TLS options: " + e.getMessage());
        }
        return tls.build();
    }

    private static final X509TrustManager TRUST_ALL = new X509TrustManager() {
        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType) {
            // Test-only: trusts everything.
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType) {
            // Test-only: trusts everything.
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return new X509Certificate[0];
        }
    };
}
