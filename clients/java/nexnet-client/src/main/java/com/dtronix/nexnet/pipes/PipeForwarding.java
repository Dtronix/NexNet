package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.session.NexusExecutors;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;

/** Byte relays between pipes: {@link NexusDuplexPipe#forwardTo} and {@link NexusDuplexPipe#bridge}. */
final class PipeForwarding {
    private PipeForwarding() {
    }

    static CompletableFuture<Void> forward(NexusDuplexPipe source, NexusDuplexPipe destination) {
        if (source == null || destination == null) {
            throw new NullPointerException(source == null ? "source" : "destination");
        }
        CompletableFuture<Void> done = new CompletableFuture<>();
        step(source, destination, done, NexusExecutors.shared());
        return done;
    }

    /** One buffer: read it, write it, wait for the destination's credit, then go on. Async, so no stack grows. */
    private static void step(NexusDuplexPipe source, NexusDuplexPipe destination, CompletableFuture<Void> done,
                             Executor executor) {
        source.read().whenCompleteAsync((buffer, readError) -> {
            if (readError != null) {
                done.completeExceptionally(PipeManager.unwrap(readError));
                return;
            }
            if (buffer == null) {
                // The source's input ended: half-close the destination. The source is left as is.
                destination.completeOutput().whenComplete((v, e) -> done.complete(null));
                return;
            }
            if (outputClosed(destination)) {
                done.complete(null);
                return;
            }
            destination.write(buffer).whenCompleteAsync((v, writeError) -> {
                if (writeError != null) {
                    if (outputClosed(destination)) {
                        done.complete(null);
                    } else {
                        done.completeExceptionally(PipeManager.unwrap(writeError));
                    }
                    return;
                }
                step(source, destination, done, executor);
            }, executor);
        }, executor);
    }

    private static boolean outputClosed(NexusDuplexPipe pipe) {
        return pipe instanceof PipeImpl && ((PipeImpl) pipe).isOutputClosed();
    }

    static CompletableFuture<Void> bridge(NexusDuplexPipe a, NexusDuplexPipe b) {
        CompletableFuture<Void> aToB = forward(a, b);
        CompletableFuture<Void> bToA = forward(b, a);
        CompletableFuture<Void> done = new CompletableFuture<>();
        CompletableFuture.allOf(aToB, bToA).whenComplete((v, error) -> {
            if (error == null) {
                done.complete(null);
                return;
            }
            // A direction failed: end both pipes' output so neither side waits for more.
            a.completeOutput();
            b.completeOutput();
            done.completeExceptionally(PipeManager.unwrap(error));
        });
        return done;
    }
}
