package com.dtronix.nexnet.metrics;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.diagnostics.TestClients;
import com.dtronix.nexnet.diagnostics.TestClients.TestProxy;
import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.pipes.PipeOptions;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.GoldenFrames;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.session.ManualTimers;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.transport.Transport;
import java.io.IOException;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeSet;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.jspecify.annotations.Nullable;

@Timeout(30)
class NexusMetricsTest {
    private final List<NexusClient<TestProxy>> clients = new ArrayList<>();
    private final NexusMetrics metrics = new NexusMetrics(new ManualTimers());

    @AfterEach
    void tearDown() throws Exception {
        metrics.close();
        for (NexusClient<TestProxy> client : clients) {
            client.close();
        }
        TestClients.awaitIdle();
    }

    static String metricsDoc() throws IOException {
        Path golden = Paths.get(System.getProperty("nexnet.golden"));
        Path doc = golden.getParent().getParent().resolve("docs").resolve("articles").resolve("metrics.md");
        return new String(Files.readAllBytes(doc), StandardCharsets.UTF_8);
    }

    @Test
    void theCatalogMatchesDocsArticlesMetrics() throws IOException {
        Map<String, String[]> documented = new HashMap<>();
        Matcher m = Pattern.compile("(?m)^\\| `(nexnet\\.[a-z_.]+)` \\| (\\w+) \\| `([^`]+)` \\|([^|]*)\\|")
            .matcher(metricsDoc());
        while (m.find()) {
            documented.put(m.group(1), new String[] {m.group(2), m.group(3), m.group(4)});
        }
        assertTrue(documented.size() > 15, "parsed " + documented.size());
        TreeSet<String> names = new TreeSet<>();
        for (NexusInstrument instrument : NexusInstruments.all()) {
            names.add(instrument.name());
            String[] doc = documented.get(instrument.name());
            assertTrue(doc != null, instrument.name());
            String kind = instrument.kind() == InstrumentKind.HISTOGRAM ? "Histogram"
                : instrument.kind() == InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER ? "UpDownCounter" : "Counter";
            assertEquals(doc[0], kind, instrument.name());
            assertEquals(doc[1], instrument.unit(), instrument.name());
            for (String tag : instrument.tags()) {
                assertTrue(doc[2].contains(tag), instrument.name() + " " + tag);
            }
            assertEquals(instrument.kind() == InstrumentKind.HISTOGRAM, instrument.buckets().length > 0);
        }
        assertEquals(new TreeSet<>(documented.keySet()), names);
        assertEquals("NexNet", NexusInstruments.METER_NAME);
    }

    @Test
    void tagValuesMatchCSharp() {
        assertEquals("socket_closed_when_writing", MetricTags.toSnakeCase("SocketClosedWhenWriting"));
        assertEquals("flow_control", MetricTags.disconnectReasonTag(DisconnectReason.FLOW_CONTROL));
        assertEquals("server_shutdown", MetricTags.disconnectReasonTag(DisconnectReason.SERVER_SHUTDOWN));
        assertEquals("unknown", MetricTags.disconnectReasonTag(DisconnectReason.UNKNOWN));
        for (DisconnectReason reason : DisconnectReason.values()) {
            String pascal = pascal(reason.name());
            assertEquals(MetricTags.toSnakeCase(pascal), MetricTags.disconnectReasonTag(reason));
        }
        assertEquals("tcp", MetricTags.transportTag(ClientConfig.tcp("h", 1).build()));
        assertEquals("tls", MetricTags.transportTag(ClientConfig.tcpTls("h", 1).build()));
        assertEquals("websocket", MetricTags.transportTag(ClientConfig.webSocket(URI.create("wss://h/n")).build()));
        assertEquals("httpsocket", MetricTags.transportTag(ClientConfig.httpSocket(URI.create("http://h/n")).build()));
    }

    private static String pascal(String upperSnake) {
        StringBuilder sb = new StringBuilder();
        for (String part : upperSnake.split("_")) {
            sb.append(part.charAt(0)).append(part.substring(1).toLowerCase(java.util.Locale.ROOT));
        }
        return sb.toString();
    }

    /** The observable instruments by name, with the direction or pool tag appended ({@code nexnet.io.bytes/sent}). */
    private Map<String, Long> collect() {
        Map<String, Long> out = new HashMap<>();
        metrics.collect((instrument, value, tags) -> {
            assertEquals("client", tags.get(MetricTags.ROLE));
            assertEquals("tcp", tags.get(MetricTags.TRANSPORT));
            String extra = tags.containsKey(MetricTags.DIRECTION) ? "/" + tags.get(MetricTags.DIRECTION)
                : tags.containsKey(MetricTags.INVOCATION_POOL) ? "/" + tags.get(MetricTags.INVOCATION_POOL) : "";
            assertTrue(out.put(instrument.name() + extra, value) == null, instrument.name() + extra);
        });
        return out;
    }

    /** A listener that records every event as text. */
    private static NexusMetricsListener recorder(List<String> pushed) {
        return new NexusMetricsListener() {
            @Override
            public void add(NexusInstrument instrument, long value, Map<String, String> tags) {
                pushed.add("add " + instrument.name() + " " + value + " " + tags);
            }

            @Override
            public void record(NexusInstrument instrument, double value, Map<String, String> tags) {
                assertTrue(value >= 0, instrument.name() + " " + value);
                pushed.add("record " + instrument.name() + " " + tags);
            }
        };
    }

    @Test
    void reportsActiveSessionsAndDisconnects() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(server);
        clients.add(client);
        List<String> pushed = new CopyOnWriteArrayList<>();
        metrics.addListener(recorder(pushed));
        metrics.track(client);
        metrics.track(client);
        assertEquals(1, metrics.trackedClients());
        assertEquals(1, MetricsCounters.subscriptions());
        assertEquals(0L, collect().get("nexnet.session.active"));

        client.connect().get(5, TimeUnit.SECONDS);
        TestClients.waitFor(() -> collect().get("nexnet.session.active") == 1L);
        Map<String, Long> live = collect();
        assertEquals(0L, live.get("nexnet.session.handshaking"));
        // The session's own counters, read live (C# CollectMetrics).
        assertEquals(1L, live.get("nexnet.io.frames/received"));
        assertEquals((long) (FakeServer.PREAMBLE.length + GoldenFrames.get("server-greeting").length),
            live.get("nexnet.io.bytes/received"));
        assertTrue(live.get("nexnet.io.frames/sent") >= 1, live.toString());
        assertTrue(live.get("nexnet.io.bytes/sent") > FakeServer.PREAMBLE.length, live.toString());
        assertEquals(InvocationOptions.defaults().sessionBudget(), live.get("nexnet.budget.limit"));
        assertTrue(live.get("nexnet.budget.reserved") > 0, "the invocation queue is reserved");
        assertEquals(0L, live.get("nexnet.invocation.queued/normal"));
        assertEquals(0L, live.get("nexnet.invocation.queued/separate"));
        assertEquals(0L, live.get("nexnet.invocation.executing/normal"));
        assertEquals(0L, live.get("nexnet.invocation.executing/separate"));
        assertEquals(0L, live.get("nexnet.invocation.credit_waits"));
        assertEquals(0L, live.get("nexnet.pipe.open"));
        assertEquals(0L, live.get("nexnet.pipe.window"));
        assertEquals(0L, live.get("nexnet.large_message.in_flight/inbound"));
        assertEquals(0L, live.get("nexnet.large_message.in_flight/outbound"));
        assertEquals(17, live.size(), live.toString());
        client.createPipe();
        Map<String, Long> withPipe = collect();
        assertEquals(1L, withPipe.get("nexnet.pipe.open"));
        assertEquals((long) PipeOptions.defaults().pipeWindow(), withPipe.get("nexnet.pipe.window"));
        assertEquals(live.get("nexnet.budget.reserved") + PipeOptions.defaults().pipeWindow(),
            (long) withPipe.get("nexnet.budget.reserved"));

        server.disconnect(0, 28);
        TestClients.waitFor(() -> pushed.size() == 2);
        Map<String, Long> ended = collect();
        assertEquals(0L, ended.get("nexnet.session.active"));
        assertEquals(0L, ended.get("nexnet.pipe.open"));
        // Monotonic: the ended session's totals stay counted (one more frame and byte: the disconnect).
        assertEquals(live.get("nexnet.io.bytes/received") + 1, (long) ended.get("nexnet.io.bytes/received"));
        assertEquals(2L, ended.get("nexnet.io.frames/received"));
        // C# order: the duration, then the disconnect.
        assertEquals("record nexnet.session.duration {nexnet.role=client, nexnet.transport=tcp, "
            + "nexnet.disconnect.reason=server_shutdown}", pushed.get(0));
        assertEquals("add nexnet.session.disconnects 1 {nexnet.role=client, nexnet.transport=tcp, "
            + "nexnet.disconnect.reason=server_shutdown, nexnet.session.phase=established}", pushed.get(1));

        // A second session is counted again.
        client.connect().get(5, TimeUnit.SECONDS);
        TestClients.waitFor(() -> collect().get("nexnet.session.active") == 1L);
        assertEquals(2, MetricsCounters.subscriptions(), "the session observer and the session's sink");
        long receivedBefore = collect().get("nexnet.io.bytes/received");
        metrics.untrack(client);
        metrics.untrack(client);
        assertEquals(0, metrics.trackedClients());
        assertEquals(0, MetricsCounters.subscriptions());
        Map<String, Long> untracked = collect();
        assertEquals(receivedBefore, untracked.get("nexnet.io.bytes/received"), "kept after untrack");
        assertEquals(0L, untracked.get("nexnet.session.active"));
        client.disconnect().get(5, TimeUnit.SECONDS);
        assertEquals(2, pushed.size());
    }

    /** A connector whose server reads the greeting and then does what {@code reply} says (nothing when null). */
    private static Transport.Connector handshakeServer(byte @Nullable [] reply) {
        return (config, timeoutMillis) -> {
            LoopbackTransport transport = new LoopbackTransport();
            Thread thread = new Thread(() -> {
                try {
                    transport.server().readHandshake();
                    if (reply != null) {
                        transport.server().write(reply);
                    }
                } catch (RuntimeException ignored) {
                    // The client closed first.
                }
            }, "test-server");
            thread.setDaemon(true);
            thread.start();
            return transport;
        };
    }

    private NexusClient<TestProxy> client(Transport.Connector connector) {
        NexusClient<TestProxy> client = TestClients.client(connector);
        clients.add(client);
        return client;
    }

    @Test
    void aSessionIsCountedFromTheTransportConnectionThroughTheHandshake() throws Exception {
        NexusClient<TestProxy> client = client(handshakeServer(null));
        List<String> pushed = new CopyOnWriteArrayList<>();
        metrics.addListener(recorder(pushed));
        metrics.track(client);
        client.tryConnect();
        TestClients.waitFor(() -> collect().get("nexnet.session.handshaking") == 1L);
        Map<String, Long> handshaking = collect();
        assertEquals(1L, handshaking.get("nexnet.session.active"));
        assertTrue(handshaking.get("nexnet.io.bytes/sent") > FakeServer.PREAMBLE.length, handshaking.toString());
        assertEquals(0L, handshaking.get("nexnet.io.frames/received"));
        client.close();
        TestClients.waitFor(() -> pushed.size() == 2);
        assertEquals(0L, collect().get("nexnet.session.handshaking"));
        assertTrue(pushed.get(1).startsWith("add nexnet.session.disconnects 1 "), pushed.toString());
        assertTrue(pushed.get(1).endsWith(", nexnet.session.phase=handshake}"), pushed.toString());
    }

    @Test
    void aDisconnectDuringTheHandshakeIsReportedWithTheHandshakePhase() throws Exception {
        byte[] reply = Arrays.copyOf(FakeServer.PREAMBLE, FakeServer.PREAMBLE.length + 1);
        reply[reply.length - 1] = (byte) DisconnectReason.SERVER_SHUTDOWN.code();
        NexusClient<TestProxy> client = client(handshakeServer(reply));
        List<String> pushed = new CopyOnWriteArrayList<>();
        metrics.addListener(recorder(pushed));
        metrics.track(client);
        assertFalse(client.tryConnect().get(5, TimeUnit.SECONDS).isSuccess());
        TestClients.waitFor(() -> pushed.size() == 2);
        assertEquals("record nexnet.session.duration {nexnet.role=client, nexnet.transport=tcp, "
            + "nexnet.disconnect.reason=server_shutdown}", pushed.get(0));
        assertEquals("add nexnet.session.disconnects 1 {nexnet.role=client, nexnet.transport=tcp, "
            + "nexnet.disconnect.reason=server_shutdown, nexnet.session.phase=handshake}", pushed.get(1));
        Map<String, Long> after = collect();
        assertEquals(0L, after.get("nexnet.session.active"));
        assertEquals((long) reply.length, after.get("nexnet.io.bytes/received"));
        assertEquals(1L, after.get("nexnet.io.frames/received"));
    }

    @Test
    void sessionEventsReachTheListenersWithTheClientTags() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(server);
        clients.add(client);
        List<String> pushed = new CopyOnWriteArrayList<>();
        metrics.addListener(recorder(pushed));
        metrics.track(client);
        client.connect().get(5, TimeUnit.SECONDS);
        NexusSession session = client.session();
        assertTrue(session != null && session.hasMetricsSinks());
        session.recordMetric(NexusInstruments.PIPE_RESETS, 1,
            Collections.singletonMap(MetricTags.RESET_REASON, "too_large"));
        session.recordMetric(NexusInstruments.PIPE_CREDIT_WAIT, 0.25, Collections.<String, String>emptyMap());
        assertEquals("add nexnet.pipe.resets 1 {nexnet.role=client, nexnet.transport=tcp, "
            + "nexnet.reset.reason=too_large}", pushed.get(0));
        assertEquals("record nexnet.pipe.credit_wait {nexnet.role=client, nexnet.transport=tcp}", pushed.get(1));
        metrics.untrack(client);
        assertFalse(session.hasMetricsSinks());
    }

    @Test
    void aThrowingListenerDoesNotBreakTheClient() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(server);
        clients.add(client);
        metrics.addListener(new NexusMetricsListener() {
            @Override
            public void add(NexusInstrument instrument, long value, Map<String, String> tags) {
                throw new IllegalStateException("listener");
            }
        }).close();
        metrics.addListener(new NexusMetricsListener() {
            @Override
            public void add(NexusInstrument instrument, long value, Map<String, String> tags) {
                throw new IllegalStateException("listener");
            }
        });
        metrics.track(client);
        client.connect().get(5, TimeUnit.SECONDS);
        server.disconnect(0, 28);
        client.disconnected().get(5, TimeUnit.SECONDS);
        assertFalse(client.disconnected().isCompletedExceptionally());
        assertSame(NexusMetrics.shared(), NexusMetrics.shared());
    }
}
