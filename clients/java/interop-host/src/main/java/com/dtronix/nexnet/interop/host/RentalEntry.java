package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.pool.RentedNexusClient;
import com.dtronix.nexnet.testnexus.scaffolding.ScaffoldingExport;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicBoolean;

/** One rental of a pooled client (control protocol §6.11). */
final class RentalEntry extends Driven {
    final PoolEntry pool;
    private final RentedNexusClient<?> rental;
    private final NexusClient<?> client;
    private final AtomicBoolean returned = new AtomicBoolean();

    RentalEntry(InteropHost host, Scope scope, String handle, PoolEntry pool, RentedNexusClient<?> rental,
                NexusClient<?> client) {
        super(host, scope, handle, pool.export);
        this.pool = pool;
        this.rental = rental;
        this.client = client;
    }

    @Override
    NexusClient<?> client() {
        if (client == null) {
            throw new IllegalStateException("The pooled client of rental " + handle + " is unknown.");
        }
        return client;
    }

    @Override
    Object proxy() {
        return rental.proxy();
    }

    @Override
    String state() {
        return Names.pascal(rental.state());
    }

    @Override
    Object pipeArgument(String pipeHandle, ScaffoldingExport.Method method) {
        throw HostError.notSupported("A rented client has no pipes: the pool owns its connection.");
    }

    CompletableFuture<Map<String, Object>> ensureConnected() {
        return rental.ensureConnected().thenApply(ok -> Protocol.map("ok", ok));
    }

    /** Returns the client to the pool once. */
    void release() {
        if (returned.compareAndSet(false, true)) {
            try {
                rental.close();
            } finally {
                pool.onReturned();
            }
        }
    }

    boolean isConnected() {
        return rental.state() == ConnectionState.CONNECTED;
    }
}
