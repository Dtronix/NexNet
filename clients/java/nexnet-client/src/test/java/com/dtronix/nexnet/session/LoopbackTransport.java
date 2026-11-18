package com.dtronix.nexnet.session;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.transport.Transport;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.concurrent.CompletableFuture;

/** An in-memory transport; {@link #server()} is the other end. */
public final class LoopbackTransport implements Transport {
    private final BytePipe toClient = new BytePipe();
    private final BytePipe toServer = new BytePipe();
    private final boolean secure;
    private final FakeServer server;
    /** Completes with the reason when the client closes the transport. */
    public final CompletableFuture<DisconnectReason> closed = new CompletableFuture<>();

    /** A plaintext transport. */
    public LoopbackTransport() {
        this(false);
    }

    /** A transport that reports the given security. */
    public LoopbackTransport(boolean secure) {
        this.secure = secure;
        toServer.readTimeoutMillis(5_000);
        this.server = new FakeServer(toServer.input(), toClient.output(), this);
    }

    /** The server end. */
    public FakeServer server() {
        return server;
    }

    void closeFromServer() {
        toClient.close();
    }

    @Override
    public InputStream input() {
        return toClient.input();
    }

    @Override
    public OutputStream output() {
        return toServer.output();
    }

    @Override
    public boolean isSecure() {
        return secure;
    }

    @Override
    public String name() {
        return "loopback";
    }

    @Override
    public String remoteAddress() {
        return "loopback";
    }

    @Override
    public void close(DisconnectReason reason) {
        toClient.close();
        toServer.close();
        closed.complete(reason);
    }
}
