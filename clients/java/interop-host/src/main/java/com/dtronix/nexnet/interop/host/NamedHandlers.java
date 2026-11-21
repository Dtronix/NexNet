package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.pipes.NexusChannelReader;
import com.dtronix.nexnet.pipes.NexusChannelWriter;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.testnexus.scaffolding.ScriptCall;
import com.dtronix.nexnet.testnexus.scaffolding.TypedCodec;
import com.dtronix.nexnet.testnexus.scaffolding.TypedJson;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ScheduledExecutorService;

/**
 * The built-in handlers of {@code named} script steps (control protocol §6.9); the C# reference is
 * {@code InProcNamedHandlers}.
 */
final class NamedHandlers {
    private NamedHandlers() {
    }

    static CompletableFuture<Void> run(ScheduledExecutorService timer, String handler, Args options, ScriptCall call,
                                       CancellationToken token) {
        Args opts = options == null ? new Args(null) : options;
        switch (handler) {
            case "pipe.echo":
                return pipeEcho(requirePipe(handler, call));
            case "pipe.drain":
                return pipeRead(requirePipe(handler, call), Long.MAX_VALUE);
            case "pipe.readExact":
                if (!opts.has("length")) {
                    throw HostError.badRequest("pipe.readExact needs the option 'length'.");
                }
                return pipeRead(requirePipe(handler, call), opts.integer("length"));
            case "channel.echo":
                return channelEcho(requireChannel(handler, call));
            case "channel.pump": {
                int count = option(handler, opts, "count", -1);
                if (count < 0) {
                    throw HostError.badRequest("channel.pump needs the option 'count'.");
                }
                int size = option(handler, opts, "size", 0);
                int delayMs = option(handler, opts, "delayMs", 0);
                NexusDuplexChannel<Object> channel = requireChannel(handler, call);
                TypedCodec<Object> codec = PipeEntry.codec(call.channelItem());
                return channelPump(timer, channel, codec, count, size, delayMs, token);
            }
            default:
                throw HostError.notSupported("The JVM host has no built-in handler '" + handler
                    + "' (pipe.echo, pipe.drain, pipe.readExact, channel.echo, channel.pump).");
        }
    }

    private static int option(String handler, Args opts, String name, int fallback) {
        if (!opts.has(name)) {
            return fallback;
        }
        long value = opts.integer(name);
        if (value < 0 || value > Integer.MAX_VALUE) {
            throw HostError.badRequest(handler + ": the option '" + name + "' is out of range (" + value + ").");
        }
        return (int) value;
    }

    private static NexusDuplexPipe requirePipe(String handler, ScriptCall call) {
        if (call.pipe() instanceof NexusDuplexPipe) {
            return (NexusDuplexPipe) call.pipe();
        }
        throw new IllegalStateException("The handler '" + handler + "' needs a method that takes a pipe or channel.");
    }

    @SuppressWarnings("unchecked")
    private static NexusDuplexChannel<Object> requireChannel(String handler, ScriptCall call) {
        if (call.pipe() instanceof NexusDuplexChannel && call.channelItem() != null) {
            return (NexusDuplexChannel<Object>) call.pipe();
        }
        throw new IllegalStateException("The handler '" + handler + "' needs a method that takes a pipe or channel.");
    }

    private static CompletableFuture<Void> pipeEcho(NexusDuplexPipe pipe) {
        return Async.repeat(() -> pipe.read().thenCompose(chunk -> chunk == null
                ? CompletableFuture.completedFuture(false)
                : pipe.write(chunk).thenApply(v -> true)))
            .thenCompose(v -> pipe.completeOutput());
    }

    private static CompletableFuture<Void> pipeRead(NexusDuplexPipe pipe, long length) {
        long[] read = {0};
        return Async.repeat(() -> {
            if (read[0] >= length) {
                return CompletableFuture.completedFuture(false);
            }
            return pipe.read().thenApply(chunk -> {
                if (chunk == null) {
                    return false;
                }
                read[0] += chunk.remaining();
                return true;
            });
        }).thenCompose(v -> pipe.completeOutput());
    }

    private static CompletableFuture<Void> channelEcho(NexusDuplexChannel<Object> channel) {
        return channel.reader().thenCombine(channel.writer(), (r, w) -> new Object[] {r, w}).thenCompose(pair -> {
            @SuppressWarnings("unchecked")
            NexusChannelReader<Object> reader = (NexusChannelReader<Object>) pair[0];
            @SuppressWarnings("unchecked")
            NexusChannelWriter<Object> writer = (NexusChannelWriter<Object>) pair[1];
            return Async.repeat(() -> reader.readItem().thenCompose(result -> result.isCompleted()
                    ? CompletableFuture.completedFuture(false)
                    : writer.write(result.value()).thenApply(v -> true)))
                .thenCompose(v -> writer.complete());
        });
    }

    private static CompletableFuture<Void> channelPump(ScheduledExecutorService timer, NexusDuplexChannel<Object> channel,
                                                       TypedCodec<Object> codec, int count, int size, int delayMs,
                                                       CancellationToken token) {
        TypedJson.Type type = codec.type();
        if (!canPump(type)) {
            throw HostError.notSupported("channel.pump cannot make items of type " + codec.expression()
                + " (integer types, string and byte[] only).");
        }
        return channel.writer().thenCompose(writer -> {
            int[] index = {0};
            boolean[] stopped = {false};
            return Async.repeat(() -> {
                int i = index[0];
                if (i >= count || stopped[0]) {
                    return CompletableFuture.completedFuture(false);
                }
                index[0] = i + 1;
                token.throwIfCancellationRequested();
                Object item = codec.fromJson(pumpItem(type, i, size));
                CompletableFuture<Boolean> written = new CompletableFuture<>();
                writer.write(item).whenComplete((v, e) -> {
                    if (e == null) {
                        written.complete(true);
                    } else if (Errors.unwrap(e) instanceof IllegalStateException) {
                        // The server completed the channel; there is nobody left to write to.
                        stopped[0] = true;
                        written.complete(false);
                    } else {
                        written.completeExceptionally(Errors.unwrap(e));
                    }
                });
                if (delayMs > 0 && i < count - 1) {
                    return written.thenCompose(more -> more
                        ? Async.delay(timer, delayMs, token).thenApply(v -> true)
                        : CompletableFuture.completedFuture(false));
                }
                return written;
            }).thenCompose(v -> writer.complete());
        });
    }

    private static boolean canPump(TypedJson.Type type) {
        if (type.kind() == TypedJson.Kind.BIN) {
            return true;
        }
        if (type.kind() != TypedJson.Kind.PRIM || type.prim() == null) {
            return false;
        }
        switch (type.prim()) {
            case SBYTE:
            case BYTE:
            case INT16:
            case UINT16:
            case INT32:
            case UINT32:
            case INT64:
            case UINT64:
            case STRING:
                return true;
            default:
                return false;
        }
    }

    /** Item {@code index} of channel.pump, as typed JSON. */
    static Object pumpItem(TypedJson.Type type, int index, int size) {
        long i = index;
        if (type.kind() == TypedJson.Kind.BIN) {
            byte[] data = new byte[size];
            for (int j = 0; j < size; j++) {
                data[j] = (byte) (j * 31L + index);
            }
            return TypedJson.base64(data);
        }
        switch (type.prim()) {
            case SBYTE:
                return (long) (byte) i;
            case BYTE:
                return i & 0xFF;
            case INT16:
                return (long) (short) i;
            case UINT16:
                return i & 0xFFFF;
            case INT32:
                return (long) (int) i;
            case UINT32:
                return i & 0xFFFFFFFFL;
            case INT64:
            case UINT64:
                return i;
            default: {
                StringBuilder sb = new StringBuilder(size);
                for (int j = 0; j < size; j++) {
                    sb.append((char) ('a' + (int) ((i + j) % 26)));
                }
                return sb.toString();
            }
        }
    }
}
