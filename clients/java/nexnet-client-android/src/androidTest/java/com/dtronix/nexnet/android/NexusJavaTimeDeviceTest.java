package com.dtronix.nexnet.android;

import static org.junit.Assert.assertEquals;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.filters.SdkSuppress;
import com.dtronix.nexnet.NexusDateOnly;
import com.dtronix.nexnet.NexusDateTimeOffset;
import com.dtronix.nexnet.NexusTimeOnly;
import com.dtronix.nexnet.NexusTimeSpan;
import java.time.Duration;
import java.time.Instant;
import java.time.LocalDate;
import java.time.LocalTime;
import java.time.OffsetDateTime;
import java.time.ZoneOffset;
import org.junit.Test;
import org.junit.runner.RunWith;

/** java.time round trips; java.time is API 26 (without desugaring). */
@RunWith(AndroidJUnit4.class)
@SdkSuppress(minSdkVersion = 26)
public class NexusJavaTimeDeviceTest {
    @Test
    public void roundTrips() {
        Instant instant = Instant.ofEpochSecond(1_700_000_000L, 123_456_700);
        assertEquals(instant, NexusJavaTime.toInstant(NexusJavaTime.toNexusDateTime(instant)));

        Duration duration = Duration.ofSeconds(-5, 300);
        NexusTimeSpan span = NexusJavaTime.toNexusTimeSpan(duration);
        assertEquals(duration, NexusJavaTime.toDuration(span));

        LocalDate date = LocalDate.of(2024, 2, 29);
        NexusDateOnly dateOnly = NexusJavaTime.toNexusDateOnly(date);
        assertEquals(date, NexusJavaTime.toLocalDate(dateOnly));

        LocalTime time = LocalTime.of(23, 59, 59, 999_999_900);
        NexusTimeOnly timeOnly = NexusJavaTime.toNexusTimeOnly(time);
        assertEquals(time, NexusJavaTime.toLocalTime(timeOnly));

        OffsetDateTime offset = OffsetDateTime.of(2024, 6, 1, 12, 30, 0, 100, ZoneOffset.ofHoursMinutes(-5, -30));
        NexusDateTimeOffset nexusOffset = NexusJavaTime.toNexusDateTimeOffset(offset);
        assertEquals(offset, NexusJavaTime.toOffsetDateTime(nexusOffset));
    }
}
