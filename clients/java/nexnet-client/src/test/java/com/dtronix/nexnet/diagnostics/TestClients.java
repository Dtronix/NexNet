package com.dtronix.nexnet.diagnostics;

import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.GoldenFrames;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.ProxyBase;
import com.dtronix.nexnet.spi.ProxyInvoker;
import com.dtronix.nexnet.transport.Transport;
import java.net.ConnectException;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.function.BooleanSupplier;

/** Real clients over in-memory transports, for the ops-feature tests. */
public final class TestClients {
    /** The handshake matching the golden server greeting. */
    public static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor(NexusRuntime.VERSION, "v2", 123456789, -987654321, new int[] {0});

    private TestClients() {
    }

    /** A proxy with no methods. */
    public static final class TestProxy extends ProxyBase {
        TestProxy(ProxyInvoker invoker) {
            super(invoker);
        }
    }

    /** A client nexus with no methods. */
    public static final class TestNexus extends ClientNexusBase<TestProxy> {
        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            throw new IllegalArgumentException("Unknown method id " + methodId);
        }
    }

    /** A connector whose server accepts every connection with the golden greeting, unless refusing. */
    public static final class Server implements Transport.Connector {
        /** Every transport opened, oldest first. */
        public final List<LoopbackTransport> transports = new CopyOnWriteArrayList<>();
        /** When true, connections fail with ConnectException. */
        public volatile boolean refuse;

        @Override
        public Transport connect(ClientConfig config, int timeoutMillis) throws ConnectException {
            if (refuse) {
                throw new ConnectException("refused");
            }
            LoopbackTransport transport = new LoopbackTransport();
            transports.add(transport);
            Thread thread = new Thread(() -> {
                try {
                    transport.server().readHandshake();
                    transport.server().write(concat(FakeServer.PREAMBLE, GoldenFrames.get("server-greeting")));
                } catch (RuntimeException ignored) {
                    // The client closed first.
                }
            }, "test-server");
            thread.setDaemon(true);
            thread.start();
            return transport;
        }

        /** Sends a disconnect message with this code on a transport. */
        public void disconnect(int index, int code) {
            transports.get(index).server().write(new byte[] {(byte) code});
        }
    }

    /** A new, unconnected client of the server. */
    public static NexusClient<TestProxy> client(Server server) {
        return client(ClientConfig.tcp("h", 1).build(), server);
    }

    /** A new, unconnected client of the server with this configuration. */
    public static NexusClient<TestProxy> client(ClientConfig config, Server server) {
        return NexusClient.create(config, HANDSHAKE, new TestNexus(), TestProxy::new, server);
    }

    /** A new, unconnected TCP client over a custom connector. */
    public static NexusClient<TestProxy> client(Transport.Connector connector) {
        return NexusClient.create(ClientConfig.tcp("h", 1).build(), HANDSHAKE, new TestNexus(), TestProxy::new,
            connector);
    }

    /** Waits up to 5 s for a condition. */
    public static void waitFor(BooleanSupplier condition) throws InterruptedException {
        long deadline = System.currentTimeMillis() + 5_000;
        while (!condition.getAsBoolean()) {
            if (System.currentTimeMillis() > deadline) {
                throw new AssertionError("Condition not met.");
            }
            Thread.sleep(5);
        }
    }

    /**
     * Waits up to 5 s for every leak-audit counter to return to 0 (D23). The counters are process-wide, so every test
     * that opens sessions, pipes or invocations must leave them at 0.
     */
    public static void awaitIdle() throws InterruptedException {
        long deadline = System.currentTimeMillis() + 5_000;
        while (!Diagnostics.isIdle()) {
            if (System.currentTimeMillis() > deadline) {
                throw new AssertionError("Counters " + Diagnostics.counters() + " did not return to 0.");
            }
            Thread.sleep(5);
        }
    }

    static byte[] concat(byte[] a, byte[] b) {
        byte[] c = new byte[a.length + b.length];
        System.arraycopy(a, 0, c, 0, a.length);
        System.arraycopy(b, 0, c, a.length, b.length);
        return c;
    }
}
