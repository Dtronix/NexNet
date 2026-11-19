package com.dtronix.nexnet.invocation;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

class CreditGateTest {
    private final CreditGate gate = new CreditGate();
    private final List<String> events = new ArrayList<>();

    private CreditGate.Waiter waiter(String name) {
        return new CreditGate.Waiter(() -> events.add("granted " + name), () -> events.add("closed " + name));
    }

    @Test
    void waitersQueuedBeforeTheGateOpensAreGrantedWhenItOpens() {
        gate.acquire(waiter("a"));
        assertTrue(events.isEmpty());
        gate.open(2);
        assertEquals(List.of("granted a"), events);
        assertEquals(1, gate.available());
    }

    @Test
    void grantsInFifoOrderAsCreditReturns() {
        gate.open(1);
        gate.acquire(waiter("a"));
        gate.acquire(waiter("b"));
        gate.acquire(waiter("c"));
        assertEquals(List.of("granted a"), events);
        assertEquals(2, gate.waiting());
        assertTrue(gate.release(1));
        assertTrue(gate.release(1));
        assertEquals(List.of("granted a", "granted b", "granted c"), events);
    }

    @Test
    void aNewCallerDoesNotOvertakeWaiters() {
        gate.open(1);
        gate.acquire(waiter("a"));
        gate.acquire(waiter("b"));
        assertTrue(gate.release(1));
        gate.acquire(waiter("c"));
        assertEquals(List.of("granted a", "granted b"), events);
    }

    @Test
    void aCanceledWaiterIsSkipped() {
        gate.open(1);
        gate.acquire(waiter("a"));
        CreditGate.Waiter b = waiter("b");
        gate.acquire(b);
        gate.acquire(waiter("c"));
        assertTrue(gate.cancel(b));
        assertFalse(gate.cancel(b));
        assertTrue(gate.release(1));
        assertEquals(List.of("granted a", "granted c"), events);
    }

    @Test
    void returningMoreThanWasTakenIsRefused() {
        gate.open(2);
        gate.acquire(waiter("a"));
        assertFalse(gate.release(2));
        assertTrue(gate.release(1));
        assertFalse(gate.release(1));
        assertFalse(gate.release(0));
    }

    @Test
    void closingFailsWaitersAndLaterCallers() {
        gate.open(1);
        gate.acquire(waiter("a"));
        gate.acquire(waiter("b"));
        gate.close();
        gate.acquire(waiter("c"));
        assertEquals(List.of("granted a", "closed b", "closed c"), events);
    }
}
