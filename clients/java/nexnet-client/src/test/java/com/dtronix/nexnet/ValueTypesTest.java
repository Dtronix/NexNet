package com.dtronix.nexnet;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

class ValueTypesTest {
    @Test
    void tuplesCompareElementWise() {
        assertEquals(Tuple2.of(1, "a"), new Tuple2<>(1, "a"));
        assertEquals(Tuple2.of(1, "a").hashCode(), new Tuple2<>(1, "a").hashCode());
        assertNotEquals(Tuple2.of(1, "a"), Tuple2.of(1, "b"));
        assertEquals(Tuple1.of(null), Tuple1.of(null));
        Tuple7<Integer, Integer, Integer, Integer, Integer, Integer, Integer> seven = Tuple7.of(1, 2, 3, 4, 5, 6, 7);
        assertEquals(Integer.valueOf(7), seven.item7());
        assertEquals("(1, 2, 3, 4, 5, 6, 7)", seven.toString());
    }

    @Test
    void versionParsesAndFormatsLikeDotNet() {
        assertEquals("1.2", NexusVersion.parse("1.2").toString());
        assertEquals("1.2.3.4", NexusVersion.parse("1.2.3.4").toString());
        assertEquals(-1, NexusVersion.parse("1.2.3").revision());
        assertTrue(NexusVersion.parse("1.10").compareTo(NexusVersion.parse("1.9")) > 0);
        assertThrows(IllegalArgumentException.class, () -> NexusVersion.parse("1"));
        assertThrows(IllegalArgumentException.class, () -> NexusVersion.parse("1.x"));
        assertThrows(IllegalArgumentException.class, () -> NexusVersion.parse("1.-2"));
    }

    @Test
    void valueTypesHaveValueEquality() {
        assertEquals(NexusDateTime.of(5, NexusDateTimeKind.UTC), NexusDateTime.of(5, NexusDateTimeKind.UTC));
        assertNotEquals(NexusDateTime.of(5, NexusDateTimeKind.UTC), NexusDateTime.of(5, NexusDateTimeKind.LOCAL));
        assertEquals(NexusDateTimeOffset.of(5, (short) 60), NexusDateTimeOffset.of(5, (short) 60));
        assertEquals(NexusTimeSpan.ofMillis(3), NexusTimeSpan.of(30_000));
        assertEquals(3, NexusTimeSpan.of(30_000).toMillis());
        assertEquals(NexusDateOnly.of(10), NexusDateOnly.of(10));
        assertEquals(NexusTimeOnly.of(10), NexusTimeOnly.of(10));
        assertEquals(NexusDecimal.of(1, 2, 3, 4), NexusDecimal.of(1, 2, 3, 4));
    }

    @Test
    void disconnectReasonsMapWireCodes() {
        assertEquals(DisconnectReason.GRACEFUL, DisconnectReason.fromCode(21));
        assertEquals(DisconnectReason.STALLED, DisconnectReason.fromCode(37));
        assertEquals(DisconnectReason.UNKNOWN, DisconnectReason.fromCode(36));
        assertEquals(DisconnectReason.UNKNOWN, DisconnectReason.fromCode(38));
        assertThrows(IllegalArgumentException.class, () -> DisconnectReason.fromCode(39));
        for (DisconnectReason reason : DisconnectReason.values()) {
            if (reason != DisconnectReason.UNKNOWN) {
                assertEquals(reason, DisconnectReason.fromCode(reason.code()));
            }
        }
    }

    @Test
    void runtimeVersionComesFromTheBuild() {
        assertTrue(NexusRuntime.VERSION.matches("\\d+\\.\\d+\\.\\d+.*"), NexusRuntime.VERSION);
    }
}
