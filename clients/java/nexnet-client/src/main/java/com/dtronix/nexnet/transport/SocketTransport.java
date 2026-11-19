package com.dtronix.nexnet.transport;

import com.dtronix.nexnet.DisconnectReason;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.Socket;
import java.security.cert.X509Certificate;
import java.util.concurrent.atomic.AtomicBoolean;
import org.jspecify.annotations.Nullable;

/** A transport over a connected socket (TCP, TLS, HttpSocket, WebSocket). */
final class SocketTransport implements Transport {
    /** Runs before the socket closes, for example to send a WebSocket close frame. */
    interface BeforeClose {
        void run(DisconnectReason reason);
    }

    private final Socket socket;
    private final InputStream input;
    private final OutputStream output;
    private final boolean secure;
    private final @Nullable X509Certificate certificate;
    private final String name;
    private final @Nullable BeforeClose beforeClose;
    private final AtomicBoolean closed = new AtomicBoolean();

    SocketTransport(Socket socket, InputStream input, OutputStream output, boolean secure,
                    @Nullable X509Certificate certificate, String name, @Nullable BeforeClose beforeClose) {
        this.socket = socket;
        this.input = input;
        this.output = output;
        this.secure = secure;
        this.certificate = certificate;
        this.name = name;
        this.beforeClose = beforeClose;
    }

    @Override
    public InputStream input() {
        return input;
    }

    @Override
    public OutputStream output() {
        return output;
    }

    @Override
    public boolean isSecure() {
        return secure;
    }

    @Override
    public @Nullable X509Certificate remoteCertificate() {
        return certificate;
    }

    @Override
    public String name() {
        return name;
    }

    @Override
    public @Nullable String remoteAddress() {
        InetAddress address = socket.getInetAddress();
        return address == null ? null : address.getHostAddress();
    }

    @Override
    public int remotePort() {
        int port = socket.getPort();
        return port == 0 ? -1 : port;
    }

    @Override
    public void close(DisconnectReason reason) {
        if (!closed.compareAndSet(false, true)) {
            return;
        }
        BeforeClose hook = beforeClose;
        if (hook != null) {
            try {
                hook.run(reason);
            } catch (RuntimeException ignored) {
                // Best effort: the socket closes regardless.
            }
        }
        try {
            socket.close();
        } catch (IOException ignored) {
            // Closing never throws.
        }
    }

    /** An input stream that returns a prefix (bytes read past an upgrade response) before the socket's bytes. */
    static final class PrefixedInputStream extends InputStream {
        private final byte[] prefix;
        private int position;
        private final InputStream rest;

        PrefixedInputStream(byte[] prefix, InputStream rest) {
            this.prefix = prefix;
            this.rest = rest;
        }

        @Override
        public int read() throws IOException {
            if (position < prefix.length) {
                return prefix[position++] & 0xFF;
            }
            return rest.read();
        }

        @Override
        public int read(byte[] b, int off, int len) throws IOException {
            if (len == 0) {
                return 0;
            }
            if (position < prefix.length) {
                int n = Math.min(len, prefix.length - position);
                System.arraycopy(prefix, position, b, off, n);
                position += n;
                return n;
            }
            return rest.read(b, off, len);
        }

        @Override
        public int available() throws IOException {
            return prefix.length - position + rest.available();
        }

        @Override
        public void close() throws IOException {
            rest.close();
        }
    }
}
