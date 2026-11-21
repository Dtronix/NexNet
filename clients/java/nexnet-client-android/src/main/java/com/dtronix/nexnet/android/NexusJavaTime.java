package com.dtronix.nexnet.android;

import androidx.annotation.RequiresApi;
import com.dtronix.nexnet.NexusDateOnly;
import com.dtronix.nexnet.NexusDateTime;
import com.dtronix.nexnet.NexusDateTimeKind;
import com.dtronix.nexnet.NexusDateTimeOffset;
import com.dtronix.nexnet.NexusTimeOnly;
import com.dtronix.nexnet.NexusTimeSpan;
import java.time.Duration;
import java.time.Instant;
import java.time.LocalDate;
import java.time.LocalTime;
import java.time.OffsetDateTime;
import java.time.ZoneOffset;

/**
 * Converts the NexNet value types (D14) to and from {@code java.time}. {@code java.time} is API 26; on API 24 and 25
 * enable core library desugaring, which provides it (lint checks the calls). The core runtime never uses
 * {@code java.time} itself.
 *
 * <p>Precision: .NET ticks are 100 ns, so nanoseconds are truncated to whole ticks on the way to NexNet.
 */
@RequiresApi(26)
public final class NexusJavaTime {
    private NexusJavaTime() {
    }

    /**
     * The instant of a date and time. A Local or Unspecified value is read as UTC ticks, as
     * {@link NexusDateTime#toEpochSecond()} does.
     *
     * @param value the value
     * @return the instant
     */
    public static Instant toInstant(NexusDateTime value) {
        return Instant.ofEpochSecond(value.toEpochSecond(), value.nanoOfSecond());
    }

    /**
     * A UTC date and time from an instant.
     *
     * @param instant the instant
     * @return the value, kind {@link NexusDateTimeKind#UTC}
     * @throws IllegalArgumentException when the instant is outside the .NET range
     */
    public static NexusDateTime toNexusDateTime(Instant instant) {
        return NexusDateTime.ofEpochSecond(instant.getEpochSecond(), instant.getNano(), NexusDateTimeKind.UTC);
    }

    /**
     * A duration.
     *
     * @param value the value
     * @return the duration
     */
    public static Duration toDuration(NexusTimeSpan value) {
        return Duration.ofSeconds(value.toSeconds(), value.nanoAdjustment());
    }

    /**
     * A time span from a duration.
     *
     * @param duration the duration
     * @return the value
     * @throws ArithmeticException when the duration does not fit in ticks
     */
    public static NexusTimeSpan toNexusTimeSpan(Duration duration) {
        return NexusTimeSpan.ofSeconds(duration.getSeconds(), duration.getNano());
    }

    /**
     * A date.
     *
     * @param value the value
     * @return the date
     */
    public static LocalDate toLocalDate(NexusDateOnly value) {
        return LocalDate.ofEpochDay(value.toEpochDay());
    }

    /**
     * A date from a local date.
     *
     * @param date the date
     * @return the value
     */
    public static NexusDateOnly toNexusDateOnly(LocalDate date) {
        return NexusDateOnly.ofEpochDay(date.toEpochDay());
    }

    /**
     * A time of day.
     *
     * @param value the value
     * @return the time
     */
    public static LocalTime toLocalTime(NexusTimeOnly value) {
        return LocalTime.ofNanoOfDay(value.toNanoOfDay());
    }

    /**
     * A time of day from a local time.
     *
     * @param time the time
     * @return the value
     */
    public static NexusTimeOnly toNexusTimeOnly(LocalTime time) {
        return NexusTimeOnly.ofNanoOfDay(time.toNanoOfDay());
    }

    /**
     * A date and time with offset.
     *
     * @param value the value
     * @return the offset date and time
     */
    public static OffsetDateTime toOffsetDateTime(NexusDateTimeOffset value) {
        return OffsetDateTime.ofInstant(Instant.ofEpochSecond(value.toEpochSecond(), value.nanoOfSecond()),
            ZoneOffset.ofTotalSeconds(value.offsetMinutes() * 60));
    }

    /**
     * A date and time with offset from an offset date and time. The offset must be whole minutes.
     *
     * @param value the offset date and time
     * @return the value
     * @throws IllegalArgumentException when the offset has seconds or the value is outside the .NET range
     */
    public static NexusDateTimeOffset toNexusDateTimeOffset(OffsetDateTime value) {
        int seconds = value.getOffset().getTotalSeconds();
        if (seconds % 60 != 0) {
            throw new IllegalArgumentException("The offset must be whole minutes: " + value.getOffset());
        }
        return NexusDateTimeOffset.ofEpochSecond(value.toEpochSecond(), value.getNano(), (short) (seconds / 60));
    }
}
