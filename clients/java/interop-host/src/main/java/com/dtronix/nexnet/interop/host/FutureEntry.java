package com.dtronix.nexnet.interop.host;

import java.util.concurrent.CompletableFuture;

/** A detached invocation ({@code invoke} with {@code detach}). */
final class FutureEntry {
    final CompletableFuture<Object> future;

    FutureEntry(CompletableFuture<Object> future) {
        this.future = future;
    }
}
