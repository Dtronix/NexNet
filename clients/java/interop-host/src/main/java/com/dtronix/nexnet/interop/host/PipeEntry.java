package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.pipes.NexusChannelReader;
import com.dtronix.nexnet.pipes.NexusChannelWriter;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.testnexus.scaffolding.TestCodecs;
import com.dtronix.nexnet.testnexus.scaffolding.TypedCodec;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;

/** A pipe of {@code pipe.create} (control protocol §6.7) and its channel views (§6.8). */
final class PipeEntry {
    private static final int CHUNK = 16 * 1024;
    private static final char[] HEX = "0123456789abcdef".toCharArray();

    final ClientEntry owner;
    final String handle;
    final NexusDuplexPipe pipe;
    private final Map<String, NexusDuplexChannel<Object>> views = new ConcurrentHashMap<>();
    private final Object lock = new Object();
    private byte[] buffered = new byte[0];
    private boolean ended;
    private volatile NexusChannelWriter<Object> writer;
    private volatile TypedCodec<Object> writerCodec;
    private volatile NexusChannelReader<Object> reader;
    private volatile TypedCodec<Object> readerCodec;
    volatile boolean disposed;

    PipeEntry(ClientEntry owner, String handle, NexusDuplexPipe pipe) {
        this.owner = owner;
        this.handle = handle;
        this.pipe = pipe;
    }

    @SuppressWarnings("unchecked")
    static TypedCodec<Object> codec(TypedCodec<?> codec) {
        return (TypedCodec<Object>) codec;
    }

    static TypedCodec<Object> codecOf(String type) {
        TypedCodec<?> codec = TestCodecs.forType(type);
        if (codec == null) {
            throw HostError.notSupported("The generated TestNexus code has no codec of the type '" + type + "'.");
        }
        return codec(codec);
    }

    /** The channel view of the pipe for items of {@code codec}. */
    NexusDuplexChannel<Object> channel(TypedCodec<?> codec) {
        TypedCodec<Object> typed = codec(codec);
        return views.computeIfAbsent(typed.expression(), k -> NexusDuplexChannel.over(pipe, typed.formatter()));
    }

    boolean isOpen() {
        return !disposed && !pipe.completed().isDone();
    }

    CompletableFuture<Map<String, Object>> write(Args args) {
        if (args.has("b64")) {
            byte[] data = Base64.getDecoder().decode(args.str("b64"));
            return pipe.write(ByteBuffer.wrap(data)).thenApply(v -> Protocol.empty());
        }
        Args pattern = args.obj("pattern");
        int seed = pattern.int32("seed");
        long length = pattern.integer("length");
        if (length < 0) {
            throw HostError.badRequest("A pattern length cannot be negative.");
        }
        long[] offset = {0};
        return Async.repeat(() -> {
            if (offset[0] >= length) {
                return CompletableFuture.completedFuture(false);
            }
            int count = (int) Math.min(CHUNK, length - offset[0]);
            byte[] chunk = new byte[count];
            for (int i = 0; i < count; i++) {
                chunk[i] = (byte) ((offset[0] + i) * 31 + seed);
            }
            offset[0] += count;
            return pipe.write(ByteBuffer.wrap(chunk)).thenApply(v -> true);
        }).thenApply(v -> Protocol.empty());
    }

    /** Appends the next chunk of input to the buffer; false at the end of input. */
    private CompletableFuture<Boolean> fill() {
        return pipe.read().thenApply(chunk -> {
            synchronized (lock) {
                if (chunk == null) {
                    ended = true;
                    return false;
                }
                byte[] bytes = new byte[chunk.remaining()];
                chunk.get(bytes);
                byte[] joined = new byte[buffered.length + bytes.length];
                System.arraycopy(buffered, 0, joined, 0, buffered.length);
                System.arraycopy(bytes, 0, joined, buffered.length, bytes.length);
                buffered = joined;
                return true;
            }
        });
    }

    CompletableFuture<Map<String, Object>> read(int min, int max) {
        if (min < 0 || max < Math.max(1, min)) {
            throw HostError.badRequest("pipe.read needs 0 <= min <= max and max >= 1.");
        }
        int need = Math.max(1, min);
        return Async.repeat(() -> {
            synchronized (lock) {
                if (buffered.length >= need || ended) {
                    return CompletableFuture.completedFuture(false);
                }
            }
            return fill();
        }).thenApply(v -> {
            byte[] taken;
            synchronized (lock) {
                int count = Math.min(buffered.length, max);
                taken = new byte[count];
                System.arraycopy(buffered, 0, taken, 0, count);
                byte[] rest = new byte[buffered.length - count];
                System.arraycopy(buffered, count, rest, 0, rest.length);
                buffered = rest;
            }
            return Protocol.map("b64", Base64.getEncoder().encodeToString(taken));
        });
    }

    CompletableFuture<Map<String, Object>> drain() {
        MessageDigest digest;
        try {
            digest = MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
        long[] length = {0};
        return Async.repeat(() -> {
            synchronized (lock) {
                digest.update(buffered);
                length[0] += buffered.length;
                buffered = new byte[0];
                if (ended) {
                    return CompletableFuture.completedFuture(false);
                }
            }
            return fill().thenApply(more -> true);
        }).thenApply(v -> Protocol.map("length", length[0], "sha256", hex(digest.digest())));
    }

    static String hex(byte[] bytes) {
        char[] chars = new char[bytes.length * 2];
        for (int i = 0; i < bytes.length; i++) {
            chars[i * 2] = HEX[(bytes[i] >> 4) & 0xF];
            chars[i * 2 + 1] = HEX[bytes[i] & 0xF];
        }
        return new String(chars);
    }

    CompletableFuture<Map<String, Object>> channelWriter(String type) {
        TypedCodec<Object> codec = codecOf(type);
        return channel(codec).writer().thenApply(w -> {
            writer = w;
            writerCodec = codec;
            return Protocol.empty();
        });
    }

    CompletableFuture<Map<String, Object>> channelWrite(List<Object> items) {
        NexusChannelWriter<Object> w = writer;
        TypedCodec<Object> codec = writerCodec;
        if (w == null || codec == null) {
            throw new HostError(HostError.INVALID_OPERATION, "channel.writer was not called for pipe " + handle + ".");
        }
        List<Object> values = new ArrayList<>(items.size());
        for (Object item : items) {
            values.add(codec.fromJson(item));
        }
        CompletableFuture<Map<String, Object>> done = new CompletableFuture<>();
        w.writeAll(values).whenComplete((v, e) -> {
            if (e == null) {
                done.complete(Protocol.map("ok", true));
            } else if (Errors.unwrap(e) instanceof IllegalStateException) {
                // The channel is complete (either side): C#'s WriteAsync returns false.
                done.complete(Protocol.map("ok", false));
            } else {
                done.completeExceptionally(Errors.unwrap(e));
            }
        });
        return done;
    }

    CompletableFuture<Map<String, Object>> channelComplete() {
        NexusChannelWriter<Object> w = writer;
        if (w == null) {
            throw new HostError(HostError.INVALID_OPERATION, "channel.writer was not called for pipe " + handle + ".");
        }
        return w.complete().thenApply(v -> Protocol.empty());
    }

    CompletableFuture<Map<String, Object>> channelReader(String type) {
        TypedCodec<Object> codec = codecOf(type);
        return channel(codec).reader().thenApply(r -> {
            reader = r;
            readerCodec = codec;
            return Protocol.empty();
        });
    }

    CompletableFuture<Map<String, Object>> channelRead(int max) {
        if (max < 1) {
            throw HostError.badRequest("channel.read needs max >= 1.");
        }
        NexusChannelReader<Object> r = requireReader();
        TypedCodec<Object> codec = readerCodec;
        return r.readBatch(max).thenApply(batch -> {
            List<Object> items = new ArrayList<>(batch.size());
            for (Object item : batch) {
                items.add(codec.toJson(item));
            }
            return Protocol.map("items", items, "complete", batch.isEmpty());
        });
    }

    CompletableFuture<Map<String, Object>> channelReadAll() {
        NexusChannelReader<Object> r = requireReader();
        TypedCodec<Object> codec = readerCodec;
        return r.readUntilComplete().thenApply(all -> {
            List<Object> items = new ArrayList<>(all.size());
            for (Object item : all) {
                items.add(codec.toJson(item));
            }
            return Protocol.map("items", items);
        });
    }

    private NexusChannelReader<Object> requireReader() {
        NexusChannelReader<Object> r = reader;
        if (r == null) {
            throw new HostError(HostError.INVALID_OPERATION, "channel.reader was not called for pipe " + handle + ".");
        }
        return r;
    }

    void dispose() {
        disposed = true;
        try {
            pipe.close();
        } catch (RuntimeException ignored) {
            // The session may be gone.
        }
    }
}
