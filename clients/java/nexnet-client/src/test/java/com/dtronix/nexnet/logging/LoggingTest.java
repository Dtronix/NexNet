package com.dtronix.nexnet.logging;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.ByteArrayOutputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.util.List;
import org.junit.jupiter.api.Test;

class LoggingTest {
    private static final String LINE = "^\\[\\d+\\.\\d{6}\\]\\[%s\\] \\[%s\\] %s$";

    @Test
    void formatsLinesLikeCSharp() {
        RollingLogger root = new RollingLogger(10, "Root");
        NexusLogger client = root.createLogger("CL1").createLogger("S2");
        client.log(NexusLogLevel.INFORMATION, "Client", null, "Connected.");
        root.log(NexusLogLevel.WARNING, null, null, "Plain.");
        List<String> lines = root.lines();
        assertEquals(2, lines.size());
        assertTrue(lines.get(0).matches(String.format(LINE, "INF", "Root\\|CL1\\|S2\\(Client\\)", "Connected\\.")),
            lines.get(0));
        assertTrue(lines.get(1).matches(String.format(LINE, "WAR", "Root", "Plain\\.")), lines.get(1));
    }

    @Test
    void levelNamesMatchCSharp() {
        String[] expected = {"TRA", "DEB", "INF", "WAR", "ERR", "CRI", "NON"};
        NexusLogLevel[] levels = NexusLogLevel.values();
        for (int i = 0; i < levels.length; i++) {
            assertEquals(expected[i], CoreLogger.shortName(levels[i]));
        }
    }

    @Test
    void appendsTheExceptionWithItsStackTrace() {
        RollingLogger root = new RollingLogger(10, null);
        root.log(NexusLogLevel.ERROR, null, new IllegalStateException("boom"), "Failed.");
        String line = root.lines().get(0);
        assertTrue(line.contains("[ERR] [] Failed. java.lang.IllegalStateException: boom"), line);
        assertTrue(line.contains("at com.dtronix.nexnet.logging.LoggingTest"), line);
    }

    @Test
    void renamingASegmentChangesTheDescendantsPaths() {
        RollingLogger root = new RollingLogger(10, "A");
        RollingLogger child = (RollingLogger) root.createLogger("B");
        RollingLogger grandchild = (RollingLogger) child.createLogger("  ");
        assertEquals("A|B", grandchild.formattedPath());
        child.pathSegment("B2");
        root.pathSegment(null);
        assertEquals("B2", grandchild.formattedPath());
        assertEquals("", root.formattedPath());
    }

    @Test
    void theMinimumLevelEnabledFlagAndBehaviorsAreSharedByTheTree() {
        RollingLogger root = new RollingLogger(10, null);
        RollingLogger child = (RollingLogger) root.createLogger("C");
        child.minLogLevel(NexusLogLevel.WARNING);
        assertEquals(NexusLogLevel.WARNING, root.minLogLevel());
        assertFalse(root.isEnabled(NexusLogLevel.INFORMATION));
        assertTrue(root.isEnabled(NexusLogLevel.ERROR));
        assertFalse(root.isEnabled(NexusLogLevel.NONE));
        root.log(NexusLogLevel.INFORMATION, null, null, "dropped");
        child.log(NexusLogLevel.ERROR, null, null, "kept");
        assertEquals(1, root.totalLinesWritten());

        root.logEnabled(false);
        assertFalse(child.logEnabled());
        child.log(NexusLogLevel.CRITICAL, null, null, "dropped");
        assertEquals(1, root.totalLinesWritten());

        child.behaviors(NexusLogBehaviors.LOG_TRANSPORT_DATA | NexusLogBehaviors.PROXY_INVOCATIONS_LOG_AS_INFO);
        assertTrue(NexusLogBehaviors.has(root.behaviors(), NexusLogBehaviors.LOG_TRANSPORT_DATA));
        assertFalse(NexusLogBehaviors.has(root.behaviors(), NexusLogBehaviors.LOCAL_INVOCATIONS_LOG_AS_INFO));
    }

    @Test
    void theRollingBufferKeepsTheLastLinesAndFlushesWithATruncationNotice() {
        RollingLogger root = new RollingLogger(3, null);
        NexusLogger child = root.createLogger("C");
        for (int i = 1; i <= 5; i++) {
            (i % 2 == 0 ? child : root).log(NexusLogLevel.INFORMATION, null, null, "m" + i);
        }
        assertEquals(5, root.totalLinesWritten());
        List<String> lines = root.lines();
        assertEquals(3, lines.size());
        assertTrue(lines.get(0).endsWith("] m3"));
        assertTrue(lines.get(2).endsWith("] m5"));

        StringBuilder out = new StringBuilder();
        root.flush(out);
        String[] written = out.toString().split("\n");
        assertEquals("Truncating Log. Showing only last 3 out of 5 total lines written.", written[0]);
        assertEquals(4, written.length);
        assertTrue(written[3].endsWith("] m5"));
        assertEquals(0, root.totalLinesWritten());
        assertTrue(root.lines().isEmpty());

        StringBuilder empty = new StringBuilder();
        root.flush(empty);
        assertEquals("", empty.toString());
        root.log(NexusLogLevel.INFORMATION, null, null, "m6");
        StringBuilder again = new StringBuilder();
        root.flush(again);
        assertFalse(again.toString().contains("Truncating"));
        assertTrue(again.toString().endsWith("] m6\n"));
    }

    @Test
    void theConsoleLoggerWritesToItsStream() {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        ConsoleLogger root = new ConsoleLogger(new PrintStream(bytes, true), "Root");
        root.createLogger("X").log(NexusLogLevel.DEBUG, "Cat", null, "hello");
        root.minLogLevel(NexusLogLevel.INFORMATION);
        root.log(NexusLogLevel.DEBUG, null, null, "dropped");
        String written = new String(bytes.toByteArray(), StandardCharsets.UTF_8).trim();
        assertTrue(written.matches(String.format(LINE, "DEB", "Root\\|X\\(Cat\\)", "hello")), written);
    }

    @Test
    void rejectsInvalidArguments() {
        assertThrows(IllegalArgumentException.class, () -> new RollingLogger(0, null));
        assertThrows(NullPointerException.class, () -> new ConsoleLogger(null, null));
        assertThrows(NullPointerException.class, () -> new RollingLogger().minLogLevel(null));
    }
}
