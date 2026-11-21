package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.Registration;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.function.Supplier;

/** Small helpers for futures. */
final class Async {
    private Async() {
    }

    static <T> CompletableFuture<T> failed(Throwable error) {
        CompletableFuture<T> future = new CompletableFuture<>();
        future.completeExceptionally(error);
        return future;
    }

    /**
     * Runs {@code body} until its future completes with false, without growing the stack when futures complete at
     * once.
     *
     * @param body one iteration: true to run another
     * @return a future that completes after the last iteration, or fails with the first failure
     */
    static CompletableFuture<Void> repeat(Supplier<CompletableFuture<Boolean>> body) {
        CompletableFuture<Void> done = new CompletableFuture<>();
        new Loop(body, done).run();
        return done;
    }

    private static final class Loop implements Runnable {
        private final Supplier<CompletableFuture<Boolean>> body;
        private final CompletableFuture<Void> done;

        Loop(Supplier<CompletableFuture<Boolean>> body, CompletableFuture<Void> done) {
            this.body = body;
            this.done = done;
        }

        @Override
        public void run() {
            while (!done.isDone()) {
                CompletableFuture<Boolean> step;
                try {
                    step = body.get();
                } catch (Throwable e) {
                    done.completeExceptionally(Errors.unwrap(e));
                    return;
                }
                if (!step.isDone()) {
                    step.whenComplete((more, error) -> {
                        if (error != null) {
                            done.completeExceptionally(Errors.unwrap(error));
                        } else if (more == null || !more) {
                            done.complete(null);
                        } else {
                            run();
                        }
                    });
                    return;
                }
                Boolean more;
                try {
                    more = step.join();
                } catch (Throwable e) {
                    done.completeExceptionally(Errors.unwrap(e));
                    return;
                }
                if (more == null || !more) {
                    done.complete(null);
                    return;
                }
            }
        }
    }

    /**
     * A delay that fails with a cancellation when the token is canceled first.
     *
     * @param timer the scheduler
     * @param millis the delay
     * @param token the token
     * @return the future
     */
    static CompletableFuture<Void> delay(ScheduledExecutorService timer, long millis, CancellationToken token) {
        CompletableFuture<Void> done = new CompletableFuture<>();
        ScheduledFuture<?> scheduled = timer.schedule(() -> done.complete(null), Math.max(0, millis),
            TimeUnit.MILLISECONDS);
        Registration registration = token.register(() -> done.completeExceptionally(
            new CancellationException("The operation was canceled.")));
        done.whenComplete((v, e) -> {
            scheduled.cancel(false);
            registration.close();
        });
        return done;
    }

    /**
     * Completes when the token is canceled.
     *
     * @param token the token
     * @return the future (never completes for a token that cannot be canceled)
     */
    static CompletableFuture<Void> whenCanceled(CancellationToken token) {
        CompletableFuture<Void> done = new CompletableFuture<>();
        Registration registration = token.register(() -> done.complete(null));
        done.whenComplete((v, e) -> registration.close());
        return done;
    }

    /**
     * A future that follows {@code source} but fails with a cancellation when the token is canceled first.
     *
     * @param source the source
     * @param token the token
     * @param <T> the result type
     * @return the future
     */
    static <T> CompletableFuture<T> withToken(CompletableFuture<T> source, CancellationToken token) {
        if (!token.canBeCanceled()) {
            return source;
        }
        CompletableFuture<T> done = new CompletableFuture<>();
        Registration registration = token.register(() -> done.completeExceptionally(
            new CancellationException("The operation was canceled.")));
        source.whenComplete((v, e) -> {
            if (e != null) {
                done.completeExceptionally(Errors.unwrap(e));
            } else {
                done.complete(v);
            }
        });
        done.whenComplete((v, e) -> registration.close());
        return done;
    }
}
