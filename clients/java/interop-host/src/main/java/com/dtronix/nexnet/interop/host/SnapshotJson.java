package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.NexusTimeSpan;
import com.dtronix.nexnet.diagnostics.NexusBudgetSnapshot;
import com.dtronix.nexnet.diagnostics.NexusInvocationSnapshot;
import com.dtronix.nexnet.diagnostics.NexusLargeMessageSnapshot;
import com.dtronix.nexnet.diagnostics.NexusPartialItemSnapshot;
import com.dtronix.nexnet.diagnostics.NexusPipeSnapshot;
import com.dtronix.nexnet.diagnostics.NexusSessionSnapshot;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * A session snapshot in the form of {@code client.snapshot} (control protocol §6.4): the field names of
 * {@code tests/golden/snapshot-fields.json}, durations in milliseconds, the session id as decimal text and enums by
 * name.
 */
final class SnapshotJson {
    private SnapshotJson() {
    }

    static Map<String, Object> of(NexusSessionSnapshot s) {
        if (s == null) {
            return null;
        }
        List<Object> pipes = new ArrayList<>();
        for (NexusPipeSnapshot pipe : s.pipes()) {
            pipes.add(pipe(pipe));
        }
        List<Object> large = new ArrayList<>();
        for (NexusLargeMessageSnapshot message : s.largeMessages()) {
            large.add(Protocol.map(
                "direction", Names.pascal(message.direction()),
                "kind", Names.pascal(message.kind()),
                "invocationId", (long) message.invocationId(),
                "length", message.length(),
                "transferred", message.transferred(),
                "sinceLastProgress", millis(message.sinceLastProgress()),
                "aborting", message.aborting()));
        }
        NexusBudgetSnapshot budget = s.budget();
        NexusInvocationSnapshot invocations = s.invocations();
        return Protocol.map(
            "id", Long.toString(s.id()),
            "isServer", s.isServer(),
            "transport", s.transport(),
            "endpoint", s.endpoint(),
            "isSecure", s.isSecure(),
            "pingInterval", millis(s.pingInterval()),
            "state", Names.pascal(s.state()),
            "isEstablished", s.isEstablished(),
            "connected", millis(s.connected()),
            "sinceLastReceived", millis(s.sinceLastReceived()),
            "sinceLastSent", millis(s.sinceLastSent()),
            "flushInProgress", millis(s.flushInProgress()),
            "budget", budget == null ? null : Protocol.map("limit", budget.limit(), "reserved", budget.reserved()),
            "invocations", invocations == null ? null : invocations(invocations),
            "pipes", pipes,
            "largeMessages", large,
            "framesSent", s.framesSent(),
            "bytesSent", s.bytesSent(),
            "framesReceived", s.framesReceived(),
            "bytesReceived", s.bytesReceived());
    }

    private static Map<String, Object> invocations(NexusInvocationSnapshot i) {
        return Protocol.map(
            "queued", (long) i.queued(),
            "executing", (long) i.executing(),
            "maxExecuting", (long) i.maxExecuting(),
            "inboundCredits", (long) i.inboundCredits(),
            "outboundCredits", (long) i.outboundCredits(),
            "creditWaits", (long) i.creditWaits(),
            "awaitingResults", (long) i.awaitingResults(),
            "creditDrops", i.creditDrops(),
            "oldestQueuedWait", millis(i.oldestQueuedWait()),
            "separatePoolQueued", (long) i.separatePoolQueued(),
            "separatePoolExecuting", (long) i.separatePoolExecuting(),
            "separatePoolMaxExecuting", (long) i.separatePoolMaxExecuting(),
            "separatePoolOldestQueuedWait", millis(i.separatePoolOldestQueuedWait()));
    }

    private static Map<String, Object> pipe(NexusPipeSnapshot p) {
        NexusPartialItemSnapshot partial = p.partialItem();
        return Protocol.map(
            "id", (long) p.id(),
            "isReady", p.isReady(),
            "readerCompleted", p.readerCompleted(),
            "writerCompleted", p.writerCompleted(),
            "closing", millis(p.closing()),
            "window", (long) p.window(),
            "receiveCredit", p.receiveCredit(),
            "buffered", p.buffered(),
            "consumedNotGranted", p.consumedNotGranted(),
            "extraOutstanding", p.extraOutstanding(),
            "sinceLastData", millis(p.sinceLastData()),
            "partialItem", partial == null ? null : Protocol.map("length", partial.length(), "received", partial.received()),
            "sendCredit", p.sendCredit(),
            "waitingForCredit", millis(p.waitingForCredit()),
            "waitingBytes", p.waitingBytes());
    }

    /** Milliseconds, with the sub-millisecond part (a tick is 100 ns); null stays null. */
    static Double millis(NexusTimeSpan span) {
        return span == null ? null : span.ticks() / 10_000.0;
    }
}
