package com.dtronix.nexnet.metrics;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.client.ClientConfig;
import java.util.Locale;

/**
 * The tag keys and value helpers of the NexNet meter, identical to C#.
 */
public final class MetricTags {
    /** {@code server} or {@code client}. */
    public static final String ROLE = "nexnet.role";
    /** The transport: {@code tcp}, {@code tls}, {@code websocket}, {@code httpsocket}, ... */
    public static final String TRANSPORT = "nexnet.transport";
    /** Server only: the endpoint name. */
    public static final String ENDPOINT = "nexnet.endpoint";
    /** {@code sent}/{@code received}, or {@code inbound}/{@code outbound}. */
    public static final String DIRECTION = "nexnet.direction";
    /** The disconnect reason in snake case. */
    public static final String DISCONNECT_REASON = "nexnet.disconnect.reason";
    /** {@code handshake} or {@code established}. */
    public static final String PHASE = "nexnet.session.phase";
    /** Server only: why a connection was rejected. */
    public static final String REJECTION_REASON = "nexnet.rejection.reason";
    /** Why a pipe was reset. */
    public static final String RESET_REASON = "nexnet.reset.reason";
    /** {@code local} or {@code remote}. */
    public static final String REFUSAL_SIDE = "nexnet.refusal.side";
    /** Why a large message was aborted. */
    public static final String ABORT_REASON = "nexnet.abort.reason";
    /** {@code normal} or {@code separate}. */
    public static final String INVOCATION_POOL = "nexnet.invocation.pool";

    private MetricTags() {
    }

    /**
     * The {@code nexnet.transport} value of a client configuration: {@code websocket}, {@code httpsocket},
     * {@code tcp} or {@code tls}.
     *
     * @param config the configuration
     * @return the tag value
     */
    public static String transportTag(ClientConfig config) {
        switch (config.transport()) {
            case WEB_SOCKET:
                return "websocket";
            case HTTP_SOCKET:
                return "httpsocket";
            case TCP:
                return "tcp";
            case TCP_TLS:
                return "tls";
            default:
                return "custom";
        }
    }

    /**
     * The {@code nexnet.disconnect.reason} value: the reason name in snake case (C# {@code ToSnakeCase} of the enum
     * name), {@code unknown} for an unassigned code.
     *
     * @param reason the reason
     * @return the tag value, for example {@code socket_error}
     */
    public static String disconnectReasonTag(DisconnectReason reason) {
        return reason.name().toLowerCase(Locale.ROOT);
    }

    /**
     * C# {@code NexusMetrics.ToSnakeCase}: {@code SocketClosedWhenWriting} becomes
     * {@code socket_closed_when_writing}.
     *
     * @param pascalCase a PascalCase name
     * @return the snake-case name
     */
    public static String toSnakeCase(String pascalCase) {
        StringBuilder sb = new StringBuilder(pascalCase.length() + 8);
        for (int i = 0; i < pascalCase.length(); i++) {
            char c = pascalCase.charAt(i);
            if (Character.isUpperCase(c)) {
                if (i > 0) {
                    sb.append('_');
                }
                sb.append(Character.toLowerCase(c));
            } else {
                sb.append(c);
            }
        }
        return sb.toString();
    }
}
