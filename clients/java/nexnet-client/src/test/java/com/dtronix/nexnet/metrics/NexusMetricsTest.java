package com.dtronix.nexnet.metrics;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.diagnostics.Diagnostics;
import com.dtronix.nexnet.diagnostics.NexusCounters;
import com.dtronix.nexnet.diagnostics.TestClients;
import com.dtronix.nexnet.diagnostics.TestClients.TestProxy;
import com.dtronix.nexnet.session.ManualTimers;
import java.io.IOException;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
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

@Timeout(30)
class NexusMetricsTest {
    private final NexusCounters baseline = Diagnostics.counters();
    private final List<NexusClient<TestProxy>> clients = new ArrayList<>();
    private final NexusMetrics metrics = new NexusMetrics(new ManualTimers());

    @AfterEach
    void tearDown() throws Exception {
        metrics.close();
        for (NexusClient<TestProxy> client : clients) {
            client.close();
        }
        TestClients.awaitBaseline(baseline);
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

    private Map<String, Long> collect() {
        Map<String, Long> out = new HashMap<>();
        metrics.collect((instrument, value, tags) -> {
            assertEquals("client", tags.get(MetricTags.ROLE));
            assertEquals("tcp", tags.get(MetricTags.TRANSPORT));
            out.put(instrument.name(), value);
        });
        return out;
    }

    @Test
    void reportsActiveSessionsAndDisconnects() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(server);
        clients.add(client);
        List<String> pushed = new CopyOnWriteArrayList<>();
        metrics.addListener(new NexusMetricsListener() {
            @Override
            public void add(NexusInstrument instrument, long value, Map<String, String> tags) {
                pushed.add("add " + instrument.name() + " " + value + " " + tags);
            }

            @Override
            public void record(NexusInstrument instrument, double value, Map<String, String> tags) {
                pushed.add("record " + instrument.name() + " " + value + " " + tags);
            }
        });
        metrics.track(client);
        metrics.track(client);
        assertEquals(1, metrics.trackedClients());
        assertEquals(1, MetricsCounters.subscriptions());
        assertEquals(0L, collect().get("nexnet.session.active"));

        client.connect().get(5, TimeUnit.SECONDS);
        TestClients.waitFor(() -> collect().get("nexnet.session.active") == 1L);
        assertEquals(0L, collect().get("nexnet.session.handshaking"));

        server.disconnect(0, 28);
        TestClients.waitFor(() -> pushed.size() == 2);
        assertEquals(0L, collect().get("nexnet.session.active"));
        assertEquals("add nexnet.session.disconnects 1 {nexnet.role=client, nexnet.transport=tcp, "
            + "nexnet.disconnect.reason=server_shutdown, nexnet.session.phase=established}", pushed.get(0));
        assertEquals("record nexnet.session.duration 0.0 {nexnet.role=client, nexnet.transport=tcp, "
            + "nexnet.disconnect.reason=server_shutdown}", pushed.get(1));

        // A second session is counted again.
        client.connect().get(5, TimeUnit.SECONDS);
        TestClients.waitFor(() -> collect().get("nexnet.session.active") == 1L);
        metrics.untrack(client);
        metrics.untrack(client);
        assertEquals(0, metrics.trackedClients());
        assertEquals(0, MetricsCounters.subscriptions());
        assertTrue(collect().isEmpty());
        client.disconnect().get(5, TimeUnit.SECONDS);
        assertEquals(2, pushed.size());
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
