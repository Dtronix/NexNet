package com.dtronix.nexnet.interop.host;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/** A leak report (control protocol §5.1). */
final class Leaks {
    int openClients;
    int openPipes;
    int timers;
    int pendingInvocations;
    int subscriptions;
    final List<Object> details = new ArrayList<>();

    Map<String, Object> toJson() {
        return Protocol.map(
            "openClients", (long) openClients,
            "openPipes", (long) openPipes,
            "timers", (long) timers,
            "pendingInvocations", (long) pendingInvocations,
            "subscriptions", (long) subscriptions,
            "details", details);
    }
}
