package com.dtronix.nexnet.okhttp;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.transport.Transport;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.InterruptedIOException;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.ProtocolException;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.security.cert.Certificate;
import java.security.cert.X509Certificate;
import java.util.List;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.locks.LockSupport;
import okhttp3.Handshake;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import okio.ByteString;
import org.jspecify.annotations.Nullable;

/**
 * A NexNet transport over an OkHttp {@link WebSocket}: the received binary messages form the input stream, and each
 * flush of the output stream sends one binary message.
 */
final class OkHttpWebSocketTransport implements Transport {
    /** WebSocket close code 1000: normal closure. */
    static final int CLOSE_NORMAL = 1000;
    /** WebSocket close code 1002: protocol error. */
    static final int CLOSE_PROTOCOL_ERROR = 1002;
    /** WebSocket close code 1003: unsupported data (a text message). */
    static final int CLOSE_UNSUPPORTED = 1003;
    /** The largest message the output stream sends; larger writes become several messages. */
    static final int MAX_MESSAGE = 64 * 1024;
    /** A flush waits while OkHttp has more than this many bytes queued (OkHttp fails the socket at 16 MiB). */
    static final long MAX_QUEUED = 1024 * 1024;

    private static final byte[] END = new byte[0];

    private final boolean secure;
    private final String name;
    private final SocketRecorder sockets;
    private final LinkedBlockingQueue<byte[]> received = new LinkedBlockingQueue<>();
    private final Object openLock = new Object();
    private final AtomicBoolean closed = new AtomicBoolean();
    private final Input input = new Input();
    private final Output output = new Output();
    private final Listener listener = new Listener();
    private volatile @Nullable WebSocket webSocket;
    private volatile @Nullable IOException failure;
    private volatile @Nullable X509Certificate certificate;
    private volatile @Nullable String remoteAddress;
    private volatile int remotePort = -1;
    /** Set once the upgrade succeeded or failed. */
    private boolean openSettled;
    private @Nullable IOException openFailure;

    OkHttpWebSocketTransport(boolean secure, String name, SocketRecorder sockets) {
        this.secure = secure;
        this.name = name;
        this.sockets = sockets;
    }

    WebSocketListener listener() {
        return listener;
    }

    /** Waits until the upgrade completes. */
    void awaitOpen(int timeoutMillis) throws IOException {
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
        synchronized (openLock) {
            while (!openSettled) {
                long left = deadline - System.nanoTime();
                if (left <= 0) {
                    throw new SocketTimeoutException("The connection timeout elapsed.");
                }
                try {
                    TimeUnit.NANOSECONDS.timedWait(openLock, left);
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    throw new InterruptedIOException("Interrupted while connecting.");
                }
            }
            IOException error = openFailure;
            if (error != null) {
                throw error;
            }
        }
    }

    private void settleOpen(@Nullable IOException error) {
        synchronized (openLock) {
            if (openSettled) {
                return;
            }
            openSettled = true;
            openFailure = error;
            openLock.notifyAll();
        }
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
        return remoteAddress;
    }

    @Override
    public int remotePort() {
        return remotePort;
    }

    @Override
    public void close(DisconnectReason reason) {
        if (!closed.compareAndSet(false, true)) {
            return;
        }
        received.offer(END);
        WebSocket ws = webSocket;
        if (ws != null) {
            int code = reason == DisconnectReason.PROTOCOL_ERROR ? CLOSE_PROTOCOL_ERROR : CLOSE_NORMAL;
            if (!ws.close(code, null)) {
                ws.cancel();
            }
        }
    }

    /** Ends the input with an error (null for a normal end of stream). */
    private void end(@Nullable IOException error) {
        if (error != null && failure == null) {
            failure = error;
        }
        received.offer(END);
    }

    /** The upgrade failure: the HTTP status for a refused upgrade, otherwise OkHttp's exception. */
    static IOException upgradeFailure(Throwable t, @Nullable Response response) {
        if (response != null && response.code() != 101) {
            int status = response.code();
            String reason = response.message();
            String message = "The server answered the upgrade with HTTP " + status
                + (reason.isEmpty() ? "" : " " + (reason.length() <= 120 ? reason : reason.substring(0, 120) + "..."))
                + ".";
            TransportError error;
            switch (status) {
                case 401:
                case 403:
                    error = TransportError.AUTHENTICATION_ERROR;
                    break;
                case 500:
                    error = TransportError.INTERNAL_ERROR;
                    break;
                default:
                    error = TransportError.PROTOCOL_ERROR;
                    break;
            }
            return new UpgradeException(new NexusTransportException(error, message, null));
        }
        if (t instanceof ProtocolException) {
            // A 101 response that breaks RFC 6455 (Sec-WebSocket-Accept, Upgrade or Connection header).
            return new UpgradeException(new NexusTransportException(TransportError.PROTOCOL_ERROR,
                "The WebSocket upgrade response is invalid: " + t.getMessage(), t));
        }
        if (t instanceof IOException) {
            return (IOException) t;
        }
        return new UpgradeException(new NexusTransportException(TransportError.PROTOCOL_ERROR,
            "The WebSocket upgrade failed: " + t, t));
    }

    /** Carries a {@link NexusTransportException} through the IOException of the open wait. */
    static final class UpgradeException extends IOException {
        private static final long serialVersionUID = 1L;

        UpgradeException(NexusTransportException cause) {
            super(cause.getMessage(), cause);
        }

        NexusTransportException transportException() {
            return (NexusTransportException) getCause();
        }
    }

    private final class Listener extends WebSocketListener {
        @Override
        public void onOpen(WebSocket ws, Response response) {
            webSocket = ws;
            Socket socket = sockets.connected();
            if (socket != null) {
                InetAddress address = socket.getInetAddress();
                if (address != null) {
                    remoteAddress = address.getHostAddress();
                    remotePort = socket.getPort() == 0 ? -1 : socket.getPort();
                }
            }
            Handshake handshake = response.handshake();
            if (handshake != null) {
                List<Certificate> peer = handshake.peerCertificates();
                if (!peer.isEmpty() && peer.get(0) instanceof X509Certificate) {
                    certificate = (X509Certificate) peer.get(0);
                }
            }
            if (response.header("Sec-WebSocket-Protocol") != null) {
                ws.cancel();
                IOException error = new UpgradeException(new NexusTransportException(TransportError.PROTOCOL_ERROR,
                    "The server selected a WebSocket subprotocol; none was requested.", null));
                end(error);
                settleOpen(error);
                return;
            }
            settleOpen(null);
            if (closed.get()) {
                ws.close(CLOSE_NORMAL, null);
            }
        }

        @Override
        public void onMessage(WebSocket ws, ByteString bytes) {
            if (bytes.size() > 0) {
                received.offer(bytes.toByteArray());
            }
        }

        @Override
        public void onMessage(WebSocket ws, String text) {
            end(new IOException("The server sent a WebSocket text message; NexNet is binary only."));
            ws.close(CLOSE_UNSUPPORTED, null);
        }

        @Override
        public void onClosing(WebSocket ws, int code, String reason) {
            end(null);
            ws.close(CLOSE_NORMAL, null);
        }

        @Override
        public void onClosed(WebSocket ws, int code, String reason) {
            end(null);
        }

        @Override
        public void onFailure(WebSocket ws, Throwable t, @Nullable Response response) {
            IOException error = upgradeFailure(t, response);
            settleOpen(error);
            end(error);
        }
    }

    /** The received messages as one byte stream. */
    private final class Input extends InputStream {
        private byte[] current = new byte[0];
        private int position;
        private boolean ended;

        @Override
        public int read() throws IOException {
            byte[] one = new byte[1];
            int n = read(one, 0, 1);
            return n < 0 ? -1 : one[0] & 0xFF;
        }

        @Override
        public int read(byte[] b, int off, int len) throws IOException {
            if (off < 0 || len < 0 || len > b.length - off) {
                throw new IndexOutOfBoundsException();
            }
            if (len == 0) {
                return 0;
            }
            while (position == current.length) {
                if (ended) {
                    return endOfStream();
                }
                byte[] next;
                try {
                    next = received.take();
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    throw new InterruptedIOException("Interrupted while reading.");
                }
                if (next == END) {
                    ended = true;
                    received.offer(END);
                    return endOfStream();
                }
                current = next;
                position = 0;
            }
            int n = Math.min(len, current.length - position);
            System.arraycopy(current, position, b, off, n);
            position += n;
            return n;
        }

        private int endOfStream() throws IOException {
            IOException error = failure;
            if (error != null && !closed.get()) {
                throw error instanceof EOFException ? error : new IOException(error.getMessage(), error);
            }
            return -1;
        }

        @Override
        public int available() {
            return current.length - position;
        }
    }

    /** Buffers writes; each flush sends the buffer as binary messages of at most {@link #MAX_MESSAGE} bytes. */
    private final class Output extends OutputStream {
        private final byte[] buffer = new byte[MAX_MESSAGE];
        private int count;

        @Override
        public void write(int b) throws IOException {
            if (count == buffer.length) {
                send();
            }
            buffer[count++] = (byte) b;
        }

        @Override
        public void write(byte[] b, int off, int len) throws IOException {
            if (off < 0 || len < 0 || len > b.length - off) {
                throw new IndexOutOfBoundsException();
            }
            while (len > 0) {
                if (count == buffer.length) {
                    send();
                }
                int n = Math.min(len, buffer.length - count);
                System.arraycopy(b, off, buffer, count, n);
                count += n;
                off += n;
                len -= n;
            }
        }

        @Override
        public void flush() throws IOException {
            if (count > 0) {
                send();
            }
        }

        private void send() throws IOException {
            WebSocket ws = webSocket;
            if (ws == null || closed.get()) {
                throw new IOException("The WebSocket is closed.");
            }
            if (!ws.send(ByteString.of(buffer, 0, count))) {
                throw new IOException("The WebSocket is closed.");
            }
            count = 0;
            // OkHttp queues sends without bound (and fails the socket past 16 MiB): wait for the queue to drain.
            while (ws.queueSize() > MAX_QUEUED) {
                if (closed.get()) {
                    throw new IOException("The WebSocket is closed.");
                }
                LockSupport.parkNanos(TimeUnit.MILLISECONDS.toNanos(1));
                if (Thread.interrupted()) {
                    Thread.currentThread().interrupt();
                    throw new InterruptedIOException("Interrupted while sending.");
                }
            }
        }
    }
}
