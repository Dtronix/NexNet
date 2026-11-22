package com.dtronix.nexnet.okhttp;

import java.io.IOException;
import java.net.InetAddress;
import java.net.Socket;
import java.util.ArrayList;
import java.util.List;
import javax.net.SocketFactory;
import org.jspecify.annotations.Nullable;

/**
 * A socket factory that records the sockets it creates for one connection, so the transport can report the peer's
 * address and port: OkHttp exposes neither on a WebSocket (its event listener is off for WebSocket calls, and the
 * {@code Response} carries no connection). Sockets come from the OkHttp client's own factory.
 */
final class SocketRecorder extends SocketFactory {
    private final SocketFactory delegate;
    private final List<Socket> sockets = new ArrayList<>();

    SocketRecorder(SocketFactory delegate) {
        this.delegate = delegate;
    }

    private Socket record(Socket socket) {
        synchronized (sockets) {
            sockets.add(socket);
        }
        return socket;
    }

    /**
     * The open, connected socket among those created: the one the upgrade went over. Fast fallback may have created
     * others; OkHttp closes them once one connects. Null when none qualifies (for example through a SOCKS proxy,
     * whose socket OkHttp creates itself).
     *
     * @return the socket, or null
     */
    @Nullable Socket connected() {
        synchronized (sockets) {
            for (Socket socket : sockets) {
                if (socket.isConnected() && !socket.isClosed()) {
                    return socket;
                }
            }
            return null;
        }
    }

    @Override
    public Socket createSocket() throws IOException {
        return record(delegate.createSocket());
    }

    @Override
    public Socket createSocket(String host, int port) throws IOException {
        return record(delegate.createSocket(host, port));
    }

    @Override
    public Socket createSocket(String host, int port, InetAddress localHost, int localPort) throws IOException {
        return record(delegate.createSocket(host, port, localHost, localPort));
    }

    @Override
    public Socket createSocket(InetAddress host, int port) throws IOException {
        return record(delegate.createSocket(host, port));
    }

    @Override
    public Socket createSocket(InetAddress address, int port, InetAddress localAddress, int localPort)
        throws IOException {
        return record(delegate.createSocket(address, port, localAddress, localPort));
    }
}
