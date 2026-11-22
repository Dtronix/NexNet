package com.dtronix.nexnet.micrometer;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.metrics.NexusInstruments;
import com.dtronix.nexnet.metrics.NexusMetrics;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.ProxyBase;
import com.dtronix.nexnet.spi.ProxyInvoker;
import com.dtronix.nexnet.transport.Transport;
import io.micrometer.core.instrument.Counter;
import io.micrometer.core.instrument.DistributionSummary;
import io.micrometer.core.instrument.FunctionCounter;
import io.micrometer.core.instrument.Gauge;
import io.micrometer.core.instrument.simple.SimpleMeterRegistry;
import java.io.ByteArrayInputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;
import org.junit.jupiter.api.Test;

class NexusMicrometerBinderTest {
    private static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor(NexusRuntime.VERSION, null, 1, 2, new int[0]);

    static final class TestProxy extends ProxyBase {
        TestProxy(ProxyInvoker invoker) {
            super(invoker);
        }
    }

    static final class TestNexus extends ClientNexusBase<TestProxy> {
        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            throw new IllegalArgumentException("Unknown method id " + methodId);
        }
    }

    /** A transport the server closes at once: the session starts and ends during the handshake. */
    static final class ClosedTransport implements Transport {
        @Override
        public InputStream input() {
            return new ByteArrayInputStream(new byte[0]);
        }

        @Override
        public OutputStream output() {
            return new OutputStream() {
                @Override
                public void write(int b) {
                }
            };
        }

        @Override
        public String name() {
            return "test";
        }

        @Override
        public void close(DisconnectReason reason) {
        }
    }

    private static NexusClient<TestProxy> client() {
        return NexusClient.create(ClientConfig.tcp("localhost", 1).build(), HANDSHAKE, new TestNexus(),
            TestProxy::new, (config, timeout) -> new ClosedTransport());
    }

    private static void waitFor(BooleanSupplier condition) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (!condition.getAsBoolean()) {
            if (System.nanoTime() > deadline) {
                throw new AssertionError("Condition not met.");
            }
            Thread.sleep(5);
        }
    }

    @Test
    void baseUnits() {
        assertEquals("bytes", NexusMicrometerBinder.baseUnit("By"));
        assertEquals("seconds", NexusMicrometerBinder.baseUnit("s"));
        assertEquals("session", NexusMicrometerBinder.baseUnit("{session}"));
        assertEquals("other", NexusMicrometerBinder.baseUnit("other"));
    }

    @Test
    void observableInstrumentsBecomeGaugesAndFunctionCounters() {
        SimpleMeterRegistry registry = new SimpleMeterRegistry();
        try (NexusMetrics metrics = new NexusMetrics();
             NexusMicrometerBinder binder = new NexusMicrometerBinder(metrics, 0);
             NexusClient<TestProxy> client = client()) {
            binder.bindTo(registry);
            binder.track(client);

            Gauge active = registry.find(NexusInstruments.SESSION_ACTIVE.name())
                .tag("nexnet.role", "client").tag("nexnet.transport", "tcp").gauge();
            assertNotNull(active);
            assertEquals(0, active.value());
            assertEquals("session", active.getId().getBaseUnit());
            assertEquals(NexusInstruments.SESSION_ACTIVE.description(), active.getId().getDescription());

            FunctionCounter sent = registry.find(NexusInstruments.IO_BYTES.name())
                .tag("nexnet.direction", "sent").functionCounter();
            assertNotNull(sent);
            assertEquals("bytes", sent.getId().getBaseUnit());

            assertNotNull(registry.find(NexusInstruments.INVOCATION_QUEUED.name())
                .tag("nexnet.invocation.pool", "separate").gauge());
            assertNotNull(registry.find(NexusInstruments.LARGE_MESSAGE_IN_FLIGHT.name())
                .tag("nexnet.direction", "outbound").gauge());
        }
    }

    @Test
    void countersAndHistogramsFollowTheEvents() throws Exception {
        SimpleMeterRegistry registry = new SimpleMeterRegistry();
        try (NexusMetrics metrics = new NexusMetrics();
             NexusMicrometerBinder binder = new NexusMicrometerBinder(metrics, 0);
             NexusClient<TestProxy> client = client()) {
            binder.bindTo(registry);
            binder.track(client);

            ConnectionResult result = client.tryConnect().get(5, TimeUnit.SECONDS);
            assertTrue(!result.isSuccess(), "The server closed the connection.");

            waitFor(() -> registry.find(NexusInstruments.SESSION_DISCONNECTS.name()).counter() != null);
            Counter disconnects = registry.find(NexusInstruments.SESSION_DISCONNECTS.name())
                .tag("nexnet.role", "client")
                .tag("nexnet.transport", "tcp")
                .tag("nexnet.session.phase", "handshake")
                .counter();
            assertNotNull(disconnects);
            assertEquals(1, disconnects.count());
            assertEquals("session", disconnects.getId().getBaseUnit());

            DistributionSummary duration = registry.find(NexusInstruments.SESSION_DURATION.name()).summary();
            assertNotNull(duration);
            assertEquals(1, duration.count());
            assertEquals("seconds", duration.getId().getBaseUnit());
            assertNotNull(duration.getId().getTag("nexnet.disconnect.reason"));
        }
    }

    @Test
    void closeRemovesTheMeters() throws Exception {
        SimpleMeterRegistry registry = new SimpleMeterRegistry();
        try (NexusMetrics metrics = new NexusMetrics();
             NexusClient<TestProxy> client = client()) {
            NexusMicrometerBinder binder = new NexusMicrometerBinder(metrics, 0);
            binder.bindTo(registry);
            binder.track(client);
            client.tryConnect().get(5, TimeUnit.SECONDS);
            waitFor(() -> registry.find(NexusInstruments.SESSION_DISCONNECTS.name()).counter() != null);
            assertTrue(registry.getMeters().size() > 2);

            binder.close();
            assertTrue(registry.getMeters().isEmpty(), "Meters left: " + registry.getMeters());
            assertNull(registry.find(NexusInstruments.SESSION_ACTIVE.name()).gauge());
            binder.close();
        }
    }

    @Test
    void aClientTrackedOnTheMetricsAppearsOnRefresh() {
        SimpleMeterRegistry registry = new SimpleMeterRegistry();
        try (NexusMetrics metrics = new NexusMetrics();
             NexusMicrometerBinder binder = new NexusMicrometerBinder(metrics);
             NexusClient<TestProxy> client = client()) {
            binder.bindTo(registry);
            assertNull(registry.find(NexusInstruments.SESSION_ACTIVE.name()).gauge());
            metrics.track(client);
            binder.refresh();
            assertNotNull(registry.find(NexusInstruments.SESSION_ACTIVE.name()).gauge());
        }
    }
}
