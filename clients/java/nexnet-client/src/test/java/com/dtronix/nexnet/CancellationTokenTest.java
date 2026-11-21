package com.dtronix.nexnet;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;

class CancellationTokenTest {
    @Test
    void noneIsNeverCanceled() {
        assertFalse(CancellationToken.NONE.canBeCanceled());
        assertFalse(CancellationToken.NONE.isCancellationRequested());
        assertSame(Registration.NONE, CancellationToken.NONE.register(() -> {
        }));
        CancellationToken.NONE.throwIfCancellationRequested();
    }

    @Test
    void cancelRunsCallbacksOnceInOrder() {
        CancellationTokenSource cts = new CancellationTokenSource();
        List<Integer> calls = new ArrayList<>();
        cts.token().register(() -> calls.add(1));
        cts.token().register(() -> calls.add(2));
        assertTrue(cts.token().canBeCanceled());

        cts.cancel();
        cts.cancel();

        assertEquals(List.of(1, 2), calls);
        assertTrue(cts.token().isCancellationRequested());
        assertThrows(CancellationException.class, cts.token()::throwIfCancellationRequested);
    }

    @Test
    void closedRegistrationIsNotCalled() {
        CancellationTokenSource cts = new CancellationTokenSource();
        AtomicInteger calls = new AtomicInteger();
        Registration registration = cts.token().register(calls::incrementAndGet);
        registration.close();
        registration.close();

        cts.cancel();

        assertEquals(0, calls.get());
    }

    @Test
    void registerAfterCancelRunsImmediately() {
        CancellationTokenSource cts = new CancellationTokenSource();
        cts.cancel();
        AtomicInteger calls = new AtomicInteger();

        cts.token().register(calls::incrementAndGet);

        assertEquals(1, calls.get());
    }

    @Test
    void throwingCallbackDoesNotStopOthers() {
        CancellationTokenSource cts = new CancellationTokenSource();
        AtomicInteger calls = new AtomicInteger();
        cts.token().register(() -> {
            throw new IllegalStateException("boom");
        });
        cts.token().register(calls::incrementAndGet);

        assertThrows(IllegalStateException.class, cts::cancel);
        assertEquals(1, calls.get());
    }
}
