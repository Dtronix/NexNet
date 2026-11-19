package com.dtronix.nexnet.transport;

import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.TransportError;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.Charset;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import org.jspecify.annotations.Nullable;

/**
 * The HTTP/1.1 upgrade exchange shared by HttpSocket and WebSocket: writes the request, reads the response head with
 * a CRLF parser capped at {@link #MAX_HEAD_BYTES}, and keeps the bytes read past {@code \r\n\r\n}, which belong to
 * the upgraded stream.
 */
final class HttpUpgrade {
    /** The largest response head accepted, status line and headers included. */
    static final int MAX_HEAD_BYTES = 16 * 1024;

    // ISO-8859-1 by name: java.nio.charset.StandardCharsets is API 19, but keep to the long-standing API.
    private static final Charset LATIN1 = Charset.forName("ISO-8859-1");

    private HttpUpgrade() {
    }

    /** A parsed response head. */
    static final class Response {
        final int status;
        final String reason;
        /** Header names in lower case; values in order of appearance. */
        final Map<String, List<String>> headers;
        /** Bytes read after the head. */
        final byte[] rest;

        Response(int status, String reason, Map<String, List<String>> headers, byte[] rest) {
            this.status = status;
            this.reason = reason;
            this.headers = headers;
            this.rest = rest;
        }

        /** All values of a header, comma-joined, or null when absent. */
        @Nullable String header(String name) {
            List<String> values = headers.get(name.toLowerCase(Locale.ROOT));
            if (values == null) {
                return null;
            }
            StringBuilder sb = new StringBuilder();
            for (String v : values) {
                if (sb.length() > 0) {
                    sb.append(", ");
                }
                sb.append(v);
            }
            return sb.toString();
        }

        /** Whether a comma-separated header contains a token, case-insensitively. */
        boolean hasToken(String name, String token) {
            String value = header(name);
            if (value == null) {
                return false;
            }
            for (String part : value.split(",")) {
                if (part.trim().equalsIgnoreCase(token)) {
                    return true;
                }
            }
            return false;
        }
    }

    /**
     * Builds the request head.
     *
     * @param target the request target (path and query)
     * @param host the Host header value
     * @param headers the headers after Host, in order
     */
    static String request(String target, String host, List<String[]> headers) {
        StringBuilder sb = new StringBuilder(256);
        sb.append("GET ").append(target).append(" HTTP/1.1\r\n");
        sb.append("Host: ").append(host).append("\r\n");
        for (String[] h : headers) {
            sb.append(h[0]).append(": ").append(h[1]).append("\r\n");
        }
        sb.append("\r\n");
        return sb.toString();
    }

    /** Writes the request and reads the response head. */
    static Response exchange(InputStream in, OutputStream out, String request) throws IOException {
        out.write(request.getBytes(LATIN1));
        out.flush();
        return readResponse(in);
    }

    static Response readResponse(InputStream in) throws IOException {
        byte[] buffer = new byte[MAX_HEAD_BYTES];
        int count = 0;
        int scanned = 0;
        while (true) {
            if (count == buffer.length) {
                throw protocolError("The upgrade response head exceeds " + MAX_HEAD_BYTES + " bytes.");
            }
            int n = in.read(buffer, count, buffer.length - count);
            if (n < 0) {
                throw new NexusTransportException(TransportError.CONNECTION_ABORTED,
                    "The server closed the connection during the upgrade.", null);
            }
            count += n;
            for (int i = Math.max(0, scanned - 3); i + 3 < count; i++) {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') {
                    int end = i + 4;
                    return parse(new String(buffer, 0, i, LATIN1), Arrays.copyOfRange(buffer, end, count));
                }
            }
            scanned = count;
        }
    }

    private static Response parse(String head, byte[] rest) {
        String[] lines = head.split("\r\n", -1);
        String status = lines[0];
        // HTTP/1.1 101 Switching Protocols
        if (!status.startsWith("HTTP/1.") || status.length() < 12 || status.charAt(8) != ' ') {
            throw protocolError("Malformed upgrade response status line: " + truncate(status));
        }
        int code;
        try {
            code = Integer.parseInt(status.substring(9, 12));
        } catch (NumberFormatException e) {
            throw protocolError("Malformed upgrade response status line: " + truncate(status));
        }
        if (status.length() > 12 && status.charAt(12) != ' ') {
            throw protocolError("Malformed upgrade response status line: " + truncate(status));
        }
        String reason = status.length() > 13 ? status.substring(13) : "";
        Map<String, List<String>> headers = new LinkedHashMap<>();
        for (int i = 1; i < lines.length; i++) {
            String line = lines[i];
            if (line.isEmpty()) {
                throw protocolError("An empty line inside the upgrade response head.");
            }
            if (line.charAt(0) == ' ' || line.charAt(0) == '\t') {
                throw protocolError("Obsolete header line folding in the upgrade response.");
            }
            int colon = line.indexOf(':');
            if (colon <= 0) {
                throw protocolError("Malformed upgrade response header: " + truncate(line));
            }
            String name = line.substring(0, colon).trim().toLowerCase(Locale.ROOT);
            String value = line.substring(colon + 1).trim();
            List<String> values = headers.get(name);
            if (values == null) {
                values = new ArrayList<>(1);
                headers.put(name, values);
            }
            values.add(value);
        }
        for (Map.Entry<String, List<String>> e : headers.entrySet()) {
            e.setValue(Collections.unmodifiableList(e.getValue()));
        }
        return new Response(code, reason, Collections.unmodifiableMap(headers), rest);
    }

    /**
     * Maps a response other than {@code 101} to the transport failure. A {@code 3xx} is a redirect, which is never
     * followed (protocol spec 13.6/13.7): it is {@code PROTOCOL_ERROR}, like every status without its own mapping.
     */
    static NexusTransportException failure(Response response) {
        String message = "The server answered the upgrade with HTTP " + response.status
            + (response.reason.isEmpty() ? "" : " " + truncate(response.reason)) + ".";
        switch (response.status) {
            case 401:
            case 403:
                return new NexusTransportException(TransportError.AUTHENTICATION_ERROR, message, null);
            case 500:
                return new NexusTransportException(TransportError.INTERNAL_ERROR, message, null);
            default:
                return new NexusTransportException(TransportError.PROTOCOL_ERROR, message, null);
        }
    }

    static NexusTransportException protocolError(String message) {
        return new NexusTransportException(TransportError.PROTOCOL_ERROR, message, null);
    }

    private static String truncate(String s) {
        return s.length() <= 120 ? s : s.substring(0, 120) + "...";
    }
}
