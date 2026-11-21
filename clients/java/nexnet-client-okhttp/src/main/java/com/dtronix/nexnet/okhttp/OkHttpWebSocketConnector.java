package com.dtronix.nexnet.okhttp;

import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.TransportKind;
import com.dtronix.nexnet.transport.Transport;
import java.io.IOException;
import java.net.URI;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import okhttp3.HttpUrl;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.WebSocket;
import org.jspecify.annotations.Nullable;

/**
 * Opens NexNet WebSocket connections ({@code ws://}, {@code wss://}) with OkHttp, for apps that already configure an
 * {@link OkHttpClient}: proxies, interceptors, DNS, certificate pinning, client certificates. Pass it to the
 * five-argument {@code NexusClient.create} (generated client factories have the same overload):
 *
 * <pre>{@code
 * OkHttpWebSocketConnector connector = new OkHttpWebSocketConnector(okHttpClient);
 * NexusClient<ServerProxy> client = NexusClient.create(
 *     ClientConfig.webSocket(URI.create("wss://example.com/nexus")).build(),
 *     handshake, nexus, ServerProxy::new, connector);
 * }</pre>
 *
 * <p>The connection is the same NexNet byte stream as the built-in WebSocket transport: binary messages, no
 * subprotocol, NexNet frames spanning messages. The upgrade request carries the configuration's headers and
 * query-string token ({@code ClientConfig.Builder.header}, {@code authorization}, {@code queryToken}).
 *
 * <p>Differences from the built-in transport:
 * <ul>
 *   <li>TLS is the {@link OkHttpClient}'s: its trust, pinning and client certificates apply, and
 *       {@link ClientConfig#tls()} is not used.</li>
 *   <li>Redirects are never followed (the upgrade fails with {@code PROTOCOL_ERROR}, as in every NexNet client), and
 *       the read timeout is off: the NexNet keepalive ({@link ClientConfig#timeoutMillis()}) detects a dead
 *       connection. The given client is not changed; the connector derives its own.</li>
 *   <li>OkHttp offers {@code permessage-deflate}; a server that accepts it compresses the messages, which changes
 *       nothing for NexNet.</li>
 *   <li>{@link Transport#remoteAddress()} is unknown (OkHttp does not expose the socket).</li>
 * </ul>
 *
 * <p>Only {@link TransportKind#WEB_SOCKET} configurations are accepted. Thread-safe; one connector may serve many
 * clients.
 */
public final class OkHttpWebSocketConnector implements Transport.Connector {
    private final OkHttpClient client;

    /**
     * Creates a connector over an OkHttp client. The client's connection pool and dispatcher are shared.
     *
     * @param client the OkHttp client
     */
    public OkHttpWebSocketConnector(OkHttpClient client) {
        if (client == null) {
            throw new NullPointerException("client");
        }
        this.client = client.newBuilder()
            .followRedirects(false)
            .followSslRedirects(false)
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .build();
    }

    /**
     * A connector over a new OkHttp client with the default settings.
     *
     * @return the connector
     */
    public static OkHttpWebSocketConnector create() {
        return new OkHttpWebSocketConnector(new OkHttpClient());
    }

    /**
     * The OkHttp client the connector uses: the given one with redirects and the read timeout turned off.
     *
     * @return the client
     */
    public OkHttpClient client() {
        return client;
    }

    @Override
    public Transport connect(ClientConfig config, int timeoutMillis) throws IOException {
        if (config.transport() != TransportKind.WEB_SOCKET) {
            throw new NexusTransportException(TransportError.INVALID_ADDRESS,
                "The OkHttp connector opens WebSocket connections only, not " + config.transport() + ".", null);
        }
        URI uri = config.uri();
        if (uri == null) {
            throw new NexusTransportException(TransportError.INVALID_ADDRESS, "The configuration has no URI.", null);
        }
        Request.Builder request = new Request.Builder().url(url(uri, config));
        for (Map.Entry<String, String> header : config.upgradeHeaders().entrySet()) {
            request.header(header.getKey(), header.getValue());
        }
        boolean secure = uri.getScheme().equalsIgnoreCase("wss");
        OkHttpWebSocketTransport transport =
            new OkHttpWebSocketTransport(secure, uri.getScheme().toLowerCase(Locale.ROOT) + "://");
        WebSocket webSocket = client.newWebSocket(request.build(), transport.listener());
        try {
            transport.awaitOpen(Math.max(1, timeoutMillis));
        } catch (OkHttpWebSocketTransport.UpgradeException e) {
            webSocket.cancel();
            throw e.transportException();
        } catch (IOException | RuntimeException e) {
            webSocket.cancel();
            throw e;
        }
        return transport;
    }

    /** The HTTP URL of the upgrade request: the URI with ws/wss as http/https, plus the query-string token. */
    static HttpUrl url(URI uri, ClientConfig config) {
        String scheme = uri.getScheme().equalsIgnoreCase("wss") ? "https" : "http";
        String rest = uri.toString().substring(uri.getScheme().length());
        HttpUrl url = HttpUrl.parse(scheme + rest);
        if (url == null) {
            throw new NexusTransportException(TransportError.INVALID_ADDRESS, "Invalid WebSocket URI: " + uri, null);
        }
        @Nullable String name = config.queryTokenName();
        @Nullable String value = config.queryTokenValue();
        if (name != null && value != null) {
            url = url.newBuilder().addQueryParameter(name, value).build();
        }
        return url;
    }
}
