package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.CancellationTokenSource;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.session.NexusExecutors;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.BiConsumer;
import java.util.function.Consumer;
import org.jspecify.annotations.Nullable;

/**
 * Byte relays between pipes: {@link NexusDuplexPipe#forwardTo} and {@link NexusDuplexPipe#bridge} (C#
 * {@code NexusPipeForwardingExtensions}).
 */
final class PipeForwarding {
    private PipeForwarding() {
    }

    static CompletableFuture<Void> forward(NexusDuplexPipe source, NexusDuplexPipe destination,
                                           CancellationToken cancellationToken) {
        return forward(source, destination, cancellationToken, new AtomicBoolean());
    }

    /**
     * @param halfClosed set just before the destination's output is completed because the source's input ended, so a
     *     bridge watching the destination knows the completion is the normal end
     */
    private static CompletableFuture<Void> forward(NexusDuplexPipe source, NexusDuplexPipe destination,
                                                   CancellationToken cancellationToken, AtomicBoolean halfClosed) {
        if (source == null || destination == null) {
            throw new NullPointerException(source == null ? "source" : "destination");
        }
        if (cancellationToken == null) {
            throw new NullPointerException("cancellationToken");
        }
        Forward forward = source instanceof PipeImpl && destination instanceof PipeImpl
            ? new Forward((PipeImpl) source, (PipeImpl) destination, cancellationToken, halfClosed)
            : new Forward(source, destination, cancellationToken, halfClosed);
        forward.start();
        return forward.done;
    }

    /**
     * One forward. Between pipes of this runtime it reads the source's buffer in place and writes only what the
     * destination's credit allows before consuming it: a slow destination stalls the source instead of buffering, and
     * a canceled forward leaves every byte it has not sent in the source. Any other pipe is copied buffer by buffer
     * with {@code read()} and {@code write()}, and cancellation is checked between buffers.
     */
    private static final class Forward {
        final NexusDuplexPipe source;
        final NexusDuplexPipe destination;
        final @Nullable PipeImpl sourceImpl;
        final @Nullable PipeImpl destinationImpl;
        final CancellationToken token;
        final AtomicBoolean halfClosed;
        final CompletableFuture<Void> done = new CompletableFuture<>();
        final Executor executor = NexusExecutors.shared();
        private final Object lock = new Object();
        /** Stops the wait the forward is in, so that its next step sees the cancellation; null while not waiting. */
        private @Nullable Runnable abandon;
        private Registration registration = Registration.NONE;

        Forward(PipeImpl source, PipeImpl destination, CancellationToken token, AtomicBoolean halfClosed) {
            this.source = source;
            this.destination = destination;
            this.sourceImpl = source;
            this.destinationImpl = destination;
            this.token = token;
            this.halfClosed = halfClosed;
        }

        Forward(NexusDuplexPipe source, NexusDuplexPipe destination, CancellationToken token,
                AtomicBoolean halfClosed) {
            this.source = source;
            this.destination = destination;
            this.sourceImpl = null;
            this.destinationImpl = null;
            this.token = token;
            this.halfClosed = halfClosed;
        }

        void start() {
            if (token.isCancellationRequested()) {
                done.completeExceptionally(canceled());
                return;
            }
            if (token.canBeCanceled()) {
                Registration r = token.register(this::stopWaiting);
                synchronized (lock) {
                    registration = r;
                }
            }
            // A future canceled by its caller also stops the forward.
            done.whenComplete((v, e) -> {
                Registration r;
                synchronized (lock) {
                    r = registration;
                    registration = Registration.NONE;
                }
                r.close();
                stopWaiting();
            });
            executor.execute(this::step);
        }

        private void step() {
            try {
                if (sourceImpl != null && destinationImpl != null) {
                    stepInPlace(sourceImpl, destinationImpl);
                } else {
                    stepByBuffer();
                }
            } catch (RuntimeException e) {
                done.completeExceptionally(e);
            }
        }

        /** False when the forward has ended or is now canceled. */
        private boolean running() {
            if (done.isDone()) {
                return false;
            }
            if (token.isCancellationRequested()) {
                done.completeExceptionally(canceled());
                return false;
            }
            return true;
        }

        private void stepInPlace(PipeImpl from, PipeImpl to) {
            if (!running()) {
                return;
            }
            if (to.isOutputClosed()) {
                done.complete(null);
                return;
            }
            // Checked before the buffer: data is never added after the input ends.
            boolean ended = from.isInputComplete();
            if (from.bufferedBytes() > 0) {
                long credit = to.sendCredit();
                if (credit <= 0) {
                    waitFor(to.waitForSendCredit(), to::abandonCreditWait);
                    return;
                }
                byte[] bytes = from.peekHead(credit);
                // Within the credit: the bytes are sent before writeBytes returns, so they can be consumed now.
                CompletableFuture<Void> sent = to.writeBytes(bytes);
                from.consume(bytes.length);
                sent.whenCompleteAsync((v, writeError) -> afterWrite(writeError), executor);
                return;
            }
            if (ended) {
                halfClose();
                return;
            }
            waitFor(from.waitForMore(0), from::abandonWait);
        }

        private void stepByBuffer() {
            if (!running()) {
                return;
            }
            source.read().whenCompleteAsync((buffer, readError) -> {
                if (readError != null) {
                    done.completeExceptionally(PipeManager.unwrap(readError));
                    return;
                }
                if (buffer == null) {
                    halfClose();
                    return;
                }
                if (outputClosed(destination)) {
                    done.complete(null);
                    return;
                }
                destination.write(buffer).whenCompleteAsync((v, writeError) -> afterWrite(writeError), executor);
            }, executor);
        }

        private void afterWrite(@Nullable Throwable writeError) {
            if (writeError == null) {
                step();
            } else if (outputClosed(destination)) {
                done.complete(null);
            } else {
                done.completeExceptionally(PipeManager.unwrap(writeError));
            }
        }

        /** The source's input ended: half-close the destination. The source is left as is. */
        private void halfClose() {
            halfClosed.set(true);
            destination.completeOutput().whenComplete((v, e) -> done.complete(null));
        }

        /** Runs the next step once {@code wait} completes; a cancellation completes it early through {@code stop}. */
        private void waitFor(CompletableFuture<Void> wait, Consumer<CompletableFuture<Void>> stop) {
            synchronized (lock) {
                abandon = () -> stop.accept(wait);
            }
            wait.whenCompleteAsync((v, e) -> {
                synchronized (lock) {
                    abandon = null;
                }
                step();
            }, executor);
            if (token.isCancellationRequested() || done.isDone()) {
                // Canceled after the step checked: the callback found no wait to stop.
                stopWaiting();
            }
        }

        private void stopWaiting() {
            Runnable stop;
            synchronized (lock) {
                stop = abandon;
                abandon = null;
            }
            if (stop != null) {
                stop.run();
            }
        }
    }

    private static CancellationException canceled() {
        return new CancellationException("The forward was canceled.");
    }

    private static boolean outputClosed(NexusDuplexPipe pipe) {
        return pipe instanceof PipeImpl && ((PipeImpl) pipe).isOutputClosed();
    }

    /**
     * Forwards {@code a} to {@code b} and {@code b} to {@code a} (C# {@code BridgeAsync}). It returns once both
     * directions half-closed. A pipe that completes while the direction writing to it is still open (a reset, its
     * session ending, or its peer completing it) ends the bridge: both pipes are completed and the future completes. A
     * direction that fails completes both pipes too, and the future fails with its error. The caller's cancellation
     * stops both directions and completes neither pipe.
     */
    static CompletableFuture<Void> bridge(NexusDuplexPipe a, NexusDuplexPipe b, CancellationToken cancellationToken) {
        if (a == null || b == null) {
            throw new NullPointerException(a == null ? "a" : "b");
        }
        if (cancellationToken == null) {
            throw new NullPointerException("cancellationToken");
        }
        CompletableFuture<Void> done = new CompletableFuture<>();
        if (cancellationToken.isCancellationRequested()) {
            done.completeExceptionally(canceled());
            return done;
        }
        CancellationTokenSource stop = new CancellationTokenSource();
        Registration link = cancellationToken.register(stop::cancel);
        AtomicBoolean aToBHalfClosed = new AtomicBoolean();
        AtomicBoolean bToAHalfClosed = new AtomicBoolean();
        CompletableFuture<Void> aToB = forward(a, b, stop.token(), aToBHalfClosed);
        CompletableFuture<Void> bToA = forward(b, a, stop.token(), bToAHalfClosed);
        AtomicBoolean ended = new AtomicBoolean();
        CompletableFuture<Void> both = CompletableFuture.allOf(aToB, bToA);
        Executor executor = NexusExecutors.shared();

        // Ends the bridge abnormally: a direction failed (failure set) or a pipe ended while still bridged (null).
        Consumer<@Nullable Throwable> end = failure -> {
            if (!ended.compareAndSet(false, true)) {
                return;
            }
            link.close();
            if (failure != null && cancellationToken.isCancellationRequested()) {
                // The caller's cancellation: stop the other direction, complete neither pipe.
                stop.cancel();
                both.handle((x, y) -> done.completeExceptionally(canceled()));
                return;
            }
            // Complete both pipes so that neither side waits for more, and stop both directions.
            completeBoth(a);
            completeBoth(b);
            stop.cancel();
            if (failure == null) {
                both.handle((x, y) -> done.complete(null));
            } else {
                Throwable cause = PipeManager.unwrap(failure);
                both.handle((x, y) -> done.completeExceptionally(cause));
            }
        };
        BiConsumer<Void, Throwable> onDirection = (v, error) -> {
            if (error != null) {
                end.accept(error);
            } else if (aToB.isDone() && bToA.isDone() && !aToB.isCompletedExceptionally()
                && !bToA.isCompletedExceptionally() && ended.compareAndSet(false, true)) {
                link.close();
                done.complete(null);
            }
        };
        aToB.whenCompleteAsync(onDirection, executor);
        bToA.whenCompleteAsync(onDirection, executor);
        // A pipe is complete. After a normal half-close the reverse direction may still be draining what it buffered,
        // so this is an abnormal end only while the direction writing to that pipe has not closed it.
        a.completed().whenCompleteAsync((v, e) -> {
            if (!bToAHalfClosed.get()) {
                end.accept(null);
            }
        }, executor);
        b.completed().whenCompleteAsync((v, e) -> {
            if (!aToBHalfClosed.get()) {
                end.accept(null);
            }
        }, executor);
        // A future canceled by its caller also stops both directions.
        done.whenComplete((v, e) -> {
            if (ended.compareAndSet(false, true)) {
                link.close();
                stop.cancel();
            }
        });
        return done;
    }

    /**
     * Completes both directions of a pipe (C# {@code CompleteAsync}); a pipe that cannot complete its input alone
     * completes its output only.
     */
    private static void completeBoth(NexusDuplexPipe pipe) {
        pipe.completeOutput();
        try {
            pipe.completeInput();
        } catch (UnsupportedOperationException e) {
            // Not a pipe of this runtime: its output is complete, which is all it supports.
        }
    }
}
