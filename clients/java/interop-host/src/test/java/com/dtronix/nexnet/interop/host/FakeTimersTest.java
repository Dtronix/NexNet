package com.dtronix.nexnet.interop.host;

import static org.junit.jupiter.api.Assertions.assertEquals;

import com.dtronix.nexnet.Registration;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Test;

class FakeTimersTest {
    @Test
    void runsDueTimersInOrderOnAdvance() {
        FakeTimers timers = new FakeTimers();
        List<String> ran = new ArrayList<>();
        long start = timers.nanoTime();
        timers.schedule(() -> ran.add("b"), 200);
        timers.schedule(() -> ran.add("a"), 100);
        timers.schedule(() -> ran.add("c"), 300);

        assertEquals(0, timers.advance(50));
        assertEquals(2, timers.advance(150));
        assertEquals(Arrays.asList("a", "b"), ran);
        assertEquals(TimeUnit.MILLISECONDS.toNanos(200), timers.nanoTime() - start);
        assertEquals(1, timers.pending());
    }

    @Test
    void runsTimersScheduledWithinTheWindow() {
        FakeTimers timers = new FakeTimers();
        List<Long> ran = new ArrayList<>();
        long start = timers.nanoTime();
        timers.schedule(() -> {
            ran.add(timers.nanoTime() - start);
            timers.schedule(() -> ran.add(timers.nanoTime() - start), 100);
        }, 100);

        assertEquals(2, timers.advance(1000));
        assertEquals(Arrays.asList(TimeUnit.MILLISECONDS.toNanos(100), TimeUnit.MILLISECONDS.toNanos(200)), ran);
    }

    @Test
    void canceledTimersDoNotRun() {
        FakeTimers timers = new FakeTimers();
        List<String> ran = new ArrayList<>();
        Registration registration = timers.schedule(() -> ran.add("x"), 10);
        registration.close();
        assertEquals(0, timers.advance(100));
        assertEquals(0, ran.size());
    }
}
