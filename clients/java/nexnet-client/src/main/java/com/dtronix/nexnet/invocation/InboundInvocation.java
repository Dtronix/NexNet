package com.dtronix.nexnet.invocation;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.CancellationTokenSource;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.spi.InvocationContext;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicBoolean;
import org.jspecify.annotations.Nullable;

/** One invocation of the client nexus by the server: the {@link InvocationContext} a generated dispatch sees. */
final class InboundInvocation implements InvocationContext {
    final InvocationRuntime runtime;
    final int invocationId;
    final int methodId;
    final int flags;
    final byte[] body;
    final int argumentsOffset;
    final int argumentsLength;
    /** Budget bytes held for a reassembled large argument, released when the invocation finishes. */
    final long reserved;
    final boolean separatePool;
    private final CancellationTokenSource cancellation;
    private final AtomicBoolean resultClaimed = new AtomicBoolean();
    /** Set once a pipe argument opened and the invocation stopped counting against its pool; guarded by the runtime. */
    boolean slotReleased;
    /** When it was queued, on the session clock; set before it is queued. */
    long queuedNanos;
    private final List<NexusDuplexPipe> pipes = new ArrayList<>();

    InboundInvocation(InvocationRuntime runtime, int invocationId, int methodId, int flags, byte[] body,
                      int argumentsOffset, int argumentsLength, long reserved, boolean separatePool,
                      CancellationTokenSource sessionCancellation) {
        this.runtime = runtime;
        this.invocationId = invocationId;
        this.methodId = methodId;
        this.flags = flags;
        this.body = body;
        this.argumentsOffset = argumentsOffset;
        this.argumentsLength = argumentsLength;
        this.reserved = reserved;
        this.separatePool = separatePool;
        // Fire-and-forget invocations can't be canceled except by the session ending: they share its token.
        this.cancellation = ignoreReturn() ? sessionCancellation : new CancellationTokenSource();
    }

    boolean ignoreReturn() {
        return (flags & InvocationRuntime.FLAG_IGNORE_RETURN) != 0;
    }

    CancellationTokenSource cancellationSource() {
        return cancellation;
    }

    /** Claims the right to send the result; true for the first caller only. */
    boolean claimResult() {
        return resultClaimed.compareAndSet(false, true);
    }

    @Override
    public CancellationToken cancellationToken() {
        return cancellation.token();
    }

    @Override
    public NexusDuplexPipe pipe(int initialPipeId) {
        return runtime.openInboundPipe(this, initialPipeId);
    }

    @Override
    public <T> NexusDuplexChannel<T> channel(int initialPipeId, Formatter<T> itemFormatter) {
        if (itemFormatter == null) {
            throw new NullPointerException("itemFormatter");
        }
        return runtime.openInboundChannel(this, initialPipeId, itemFormatter);
    }

    void addPipe(NexusDuplexPipe pipe) {
        synchronized (pipes) {
            pipes.add(pipe);
        }
    }

    /** Closes the pipes opened for this invocation's arguments. */
    void closePipes() {
        List<NexusDuplexPipe> opened;
        synchronized (pipes) {
            opened = new ArrayList<>(pipes);
            pipes.clear();
        }
        for (NexusDuplexPipe pipe : opened) {
            pipe.close();
        }
    }

    @Override
    public <R> CompletableFuture<Void> result(CompletableFuture<R> result, Formatter<R> formatter) {
        if (formatter == null) {
            throw new NullPointerException("formatter");
        }
        return complete(result, formatter);
    }

    @Override
    public CompletableFuture<Void> result(CompletableFuture<Void> result) {
        return complete(result, null);
    }

    @Override
    public CompletableFuture<Void> noResult() {
        return CompletableFuture.completedFuture(null);
    }

    private <R> CompletableFuture<Void> complete(CompletableFuture<R> result, @Nullable Formatter<R> formatter) {
        if (result == null) {
            throw new NullPointerException("The method returned a null future.");
        }
        if (ignoreReturn()) {
            // No result is sent; a failure is logged when the invocation finishes.
            return result.<Void>thenApply(value -> null);
        }
        if (!claimResult()) {
            throw new IllegalStateException("The result of this invocation has already been set.");
        }
        CompletableFuture<Void> finished = new CompletableFuture<>();
        result.whenComplete((value, error) -> runtime.sendResultLater(this, value, formatter, error, finished));
        return finished;
    }
}
