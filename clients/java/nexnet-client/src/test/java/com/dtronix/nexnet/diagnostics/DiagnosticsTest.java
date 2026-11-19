package com.dtronix.nexnet.diagnostics;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.NexusTimeSpan;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.diagnostics.TestClients.TestProxy;
import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.pipes.PipeOptions;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.GoldenFrames;
import com.dtronix.nexnet.session.SessionOptions;
import java.io.IOException;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(30)
class DiagnosticsTest {
    /** The records of tests/golden/snapshot-fields.json, in file order. */
    static Map<String, List<String>> goldenFields() throws IOException {
        String json = new String(Files.readAllBytes(Paths.get(System.getProperty("nexnet.golden"),
            "snapshot-fields.json")), StandardCharsets.UTF_8);
        String records = json.substring(json.indexOf("\"records\""));
        Map<String, List<String>> result = new LinkedHashMap<>();
        Matcher m = Pattern.compile("\"(\\w+)\"\\s*:\\s*\\[([^\\]]*)\\]").matcher(records);
        while (m.find()) {
            List<String> names = new ArrayList<>();
            Matcher name = Pattern.compile("\"(\\w+)\"").matcher(m.group(2));
            while (name.find()) {
                names.add(name.group(1));
            }
            result.put(m.group(1), names);
        }
        return result;
    }

    @Test
    void snapshotFieldsMatchTheGoldenFile() throws Exception {
        Map<String, List<String>> golden = goldenFields();
        Map<String, Class<?>> classes = new LinkedHashMap<>();
        classes.put("NexusSessionSnapshot", NexusSessionSnapshot.class);
        classes.put("NexusBudgetSnapshot", NexusBudgetSnapshot.class);
        classes.put("NexusInvocationSnapshot", NexusInvocationSnapshot.class);
        classes.put("NexusPipeSnapshot", NexusPipeSnapshot.class);
        classes.put("NexusPartialItemSnapshot", NexusPartialItemSnapshot.class);
        classes.put("NexusLargeMessageSnapshot", NexusLargeMessageSnapshot.class);
        assertEquals(new ArrayList<>(classes.keySet()), new ArrayList<>(golden.keySet()));
        for (Map.Entry<String, Class<?>> entry : classes.entrySet()) {
            Class<?> type = entry.getValue();
            List<String> expected = golden.get(entry.getKey());
            @SuppressWarnings("unchecked")
            List<String> declared = (List<String>) type.getField("FIELDS").get(null);
            assertEquals(expected, declared, entry.getKey() + ".FIELDS");
            List<String> fields = new ArrayList<>();
            for (Field field : type.getDeclaredFields()) {
                if (!Modifier.isStatic(field.getModifiers())) {
                    fields.add(field.getName());
                }
            }
            Collections.sort(fields);
            List<String> sorted = new ArrayList<>(expected);
            Collections.sort(sorted);
            assertEquals(sorted, fields, entry.getKey() + " fields");
            for (String name : expected) {
                Method accessor = type.getMethod(name);
                assertTrue(Modifier.isPublic(accessor.getModifiers()), name);
            }
            assertEquals(expected.size(), type.getConstructors()[0].getParameterCount(), entry.getKey());
        }
    }

    @Test
    void snapshotsAreImmutableValues() {
        NexusPipeSnapshot pipe = new NexusPipeSnapshot(513, true, false, false, null, 65_536, 1, 2, 3, 4,
            NexusTimeSpan.ofMillis(5), new NexusPartialItemSnapshot(10, 4), 6, null, 0);
        List<NexusPipeSnapshot> pipes = new ArrayList<>(Arrays.asList(pipe));
        NexusSessionSnapshot a = new NexusSessionSnapshot(1, false, "tcp", null, false, NexusTimeSpan.ofMillis(1_000),
            ConnectionState.CONNECTED, true,
            null, null, null, null, new NexusBudgetSnapshot(100, 10), null, pipes, Collections.emptyList(), 1L, 2L,
            3L, 4L);
        pipes.clear();
        assertEquals(1, a.pipes().size());
        assertThrows(UnsupportedOperationException.class, () -> a.pipes().clear());
        NexusSessionSnapshot b = new NexusSessionSnapshot(1, false, "tcp", null, false, NexusTimeSpan.ofMillis(1_000),
            ConnectionState.CONNECTED, true,
            null, null, null, null, new NexusBudgetSnapshot(100, 10), null, Arrays.asList(pipe),
            Collections.emptyList(), 1L, 2L, 3L, 4L);
        assertEquals(a, b);
        assertEquals(a.hashCode(), b.hashCode());
        assertTrue(a.toString().startsWith("NexusSessionSnapshot[id=1, isServer=false, transport=tcp"), a.toString());
        assertEquals(4, pipe.partialItem().received());
    }

    @Test
    void aClientSnapshotReportsTheSessionWhileConnected() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(server);
        try {
            assertNull(Diagnostics.snapshot(client));
            client.connect().get(5, TimeUnit.SECONDS);
            NexusSessionSnapshot snapshot = Diagnostics.snapshot(client);
            assertNotNull(snapshot);
            assertEquals(42, snapshot.id());
            assertFalse(snapshot.isServer());
            assertEquals("tcp", snapshot.transport());
            assertNull(snapshot.endpoint());
            assertEquals(Boolean.FALSE, snapshot.isSecure());
            NexusTimeSpan ping = snapshot.pingInterval();
            assertNotNull(ping);
            assertTrue(ping.toMillis() > 0, ping.toString());
            assertEquals(ConnectionState.CONNECTED, snapshot.state());
            assertTrue(snapshot.isEstablished());
            assertTrue(snapshot.pipes().isEmpty());
            // No nulls where C# has values (K20).
            assertNotNull(snapshot.connected());
            assertNotNull(snapshot.sinceLastReceived());
            assertNotNull(snapshot.sinceLastSent(), "the greeting was written");
            assertTrue(snapshot.connected().toMillis() >= snapshot.sinceLastReceived().toMillis());
            NexusBudgetSnapshot budget = snapshot.budget();
            assertNotNull(budget);
            assertEquals(InvocationOptions.defaults().sessionBudget(), budget.limit());
            assertTrue(budget.reserved() > 0 && budget.reserved() <= budget.limit(), budget.toString());
            NexusInvocationSnapshot invocations = snapshot.invocations();
            assertNotNull(invocations);
            assertEquals(0, invocations.queued());
            assertEquals(0, invocations.executing());
            assertEquals(InvocationOptions.defaults().maxConcurrentInvocations(), invocations.maxExecuting());
            assertEquals(SessionOptions.defaults().invocationCredits(), invocations.inboundCredits());
            assertTrue(invocations.outboundCredits() > 0, invocations.toString());
            assertEquals(0, invocations.creditWaits());
            assertEquals(0, invocations.awaitingResults());
            assertEquals(0, invocations.creditDrops());
            assertNull(invocations.oldestQueuedWait());
            assertEquals(InvocationOptions.defaults().maxSeparatePoolInvocations(),
                invocations.separatePoolMaxExecuting());
            assertNull(invocations.separatePoolOldestQueuedWait());
            assertTrue(snapshot.largeMessages().isEmpty());
            assertEquals(Long.valueOf(1), snapshot.framesReceived());
            assertEquals(Long.valueOf(FakeServer.PREAMBLE.length + GoldenFrames.get("server-greeting").length),
                snapshot.bytesReceived());
            Long framesSent = snapshot.framesSent();
            Long bytesSent = snapshot.bytesSent();
            assertNotNull(framesSent);
            assertNotNull(bytesSent);
            assertTrue(framesSent >= 1 && bytesSent > FakeServer.PREAMBLE.length, framesSent + " " + bytesSent);
            client.createPipe();
            NexusSessionSnapshot withPipe = Diagnostics.snapshot(client);
            assertNotNull(withPipe);
            assertEquals(1, withPipe.pipes().size());
            NexusPipeSnapshot pipe = withPipe.pipes().get(0);
            assertFalse(pipe.isReady());
            assertEquals(PipeOptions.defaults().pipeWindow(), pipe.window());
            assertEquals(budget.reserved() + PipeOptions.defaults().pipeWindow(), withPipe.budget().reserved());
            assertEquals(baseline.pipes() + 1, Diagnostics.counters().pipes());
            assertFalse(Diagnostics.isIdle());
            assertEquals(1, Diagnostics.counters().sessions());
            client.disconnect().get(5, TimeUnit.SECONDS);
            assertNull(Diagnostics.snapshot(client));
        } finally {
            client.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void registeredSourcesAddToTheCounters() throws Exception {
        TestClients.awaitIdle();
        Registration registration = Diagnostics.registerCounterSource(new CounterSource() {
            @Override
            public int pipes() {
                return 2;
            }

            @Override
            public int timers() {
                return 3;
            }

            @Override
            public int subscriptions() {
                return 4;
            }
        });
        NexusCounters counters = Diagnostics.counters();
        assertEquals(2, counters.pipes());
        assertEquals(3, counters.timers());
        assertEquals(4, counters.subscriptions());
        assertFalse(counters.isIdle());
        assertTrue(counters.toString().contains("pipes=2"));
        registration.close();
        assertTrue(Diagnostics.isIdle());
        assertThrows(NullPointerException.class, () -> Diagnostics.registerCounterSource(null));
    }
}
