package com.dtronix.nexnet.spi;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.collections.NexusCollectionMode;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.serialization.MsgPackWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import org.junit.jupiter.api.Test;

/**
 * Hand-written stand-ins for what the Java emitters (K03/K04) generate, compiled against the contract. If this file
 * stops compiling, the generated-code contract changed.
 */
class GeneratedCodeShapeTest {
    /** Stand-in for a generated built-in formatter constant. */
    static final Formatter<Integer> INT = new Formatter<>() {
        @Override
        public void write(MsgPackWriter writer, Integer value) {
            writer.writeInt(value);
        }

        @Override
        public Integer read(MsgPackReader reader) {
            return reader.readInt();
        }
    };

    /** Stand-in for {@code <Export>Handshake}. */
    static final HandshakeDescriptor HANDSHAKE = new HandshakeDescriptor("0.20.0", "v2", 123, -456, new int[] {2, 0, 1});

    /** Stand-in for {@code <Server>Proxy}. */
    static final class ServerProxy extends ProxyBase {
        ServerProxy(ProxyInvoker invoker) {
            super(invoker);
        }

        void fireAndForget(int value) {
            invokeVoid(0, w -> {
                w.writeArrayHeader(1);
                INT.write(w, value);
            });
        }

        CompletableFuture<Void> task() {
            return invokeTask(1, ArgumentWriter.NONE, null);
        }

        CompletableFuture<Integer> add(int a, int b) {
            return add(a, b, CancellationToken.NONE);
        }

        CompletableFuture<Integer> add(int a, int b, CancellationToken cancellationToken) {
            return invoke(2, w -> {
                w.writeArrayHeader(2);
                INT.write(w, a);
                INT.write(w, b);
            }, INT, cancellationToken);
        }

        CompletableFuture<Void> upload(NexusDuplexPipe pipe) {
            return invokePipe(3, pipe, (w, pipeId) -> {
                w.writeArrayHeader(1);
                w.writeInt(pipeId);
            });
        }

        CompletableFuture<Void> stream(NexusDuplexChannel<Integer> channel) {
            return invokeChannel(4, channel, (w, pipeId) -> {
                w.writeArrayHeader(1);
                w.writeInt(pipeId);
            });
        }

        NexusList<Integer> numbers() {
            return list(5, NexusCollectionMode.BI_DIRECTIONAL, INT);
        }
    }

    /** Stand-in for {@code <Client>NexusBase}. */
    abstract static class ClientNexusBaseGenerated extends ClientNexusBase<ServerProxy> {
        protected abstract CompletableFuture<Integer> square(int value, CancellationToken cancellationToken);

        @Override
        protected boolean separatePool(int methodId) {
            return methodId == 1;
        }

        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            switch (methodId) {
                case 0: {
                    arguments.readArrayHeader();
                    int value = INT.read(arguments);
                    return context.result(square(value, context.cancellationToken()), INT);
                }
                default:
                    throw new IllegalArgumentException("Unknown method id " + methodId);
            }
        }
    }

    /** What the user writes. */
    static final class MyNexus extends ClientNexusBaseGenerated {
        @Override
        protected CompletableFuture<Integer> square(int value, CancellationToken cancellationToken) {
            return CompletableFuture.completedFuture(value * value);
        }
    }

    @Test
    void proxyDelegatesToTheInvokerWithMethodIds() {
        RecordingInvoker invoker = new RecordingInvoker();
        ServerProxy proxy = new ServerProxy(invoker);

        proxy.fireAndForget(1);
        proxy.task();
        proxy.add(1, 2);
        proxy.upload(null);
        proxy.stream(null);
        proxy.numbers();

        assertEquals(List.of("void:0", "task:1", "invoke:2", "pipe:3", "channel:4", "list:5:BI_DIRECTIONAL"), invoker.calls);
    }

    @Test
    void handshakeDescriptorSortsAndChecksIds() {
        assertTrue(HANDSHAKE.acceptsMethod(2));
        assertFalse(HANDSHAKE.acceptsMethod(3));
        assertEquals(0, HANDSHAKE.clientAcceptedMethodIds()[0]);
        assertThrows(IllegalArgumentException.class,
            () -> new HandshakeDescriptor("0.20.0", "12345678901234567", 0, 0, new int[0]));
        assertThrows(IllegalArgumentException.class,
            () -> new HandshakeDescriptor("0.20.0", null, 0, 0, new int[] {65536}));
    }

    @Test
    void contextIsUnavailableBeforeConnect() {
        MyNexus nexus = new MyNexus();
        assertThrows(IllegalStateException.class, nexus::context);
        assertTrue(ClientNexusHooks.connected(nexus, false).isDone());
        assertTrue(ClientNexusHooks.separatePool(nexus, 1));
        assertFalse(ClientNexusHooks.separatePool(nexus, 0));
    }

    private static final class RecordingInvoker implements ProxyInvoker {
        final List<String> calls = new ArrayList<>();

        @Override
        public <R> CompletableFuture<R> invoke(int methodId, ArgumentWriter arguments, Formatter<R> result,
                                               CancellationToken cancellationToken) {
            calls.add("invoke:" + methodId);
            return new CompletableFuture<>();
        }

        @Override
        public CompletableFuture<Void> invokeTask(int methodId, ArgumentWriter arguments,
                                                  CancellationToken cancellationToken) {
            calls.add("task:" + methodId);
            return new CompletableFuture<>();
        }

        @Override
        public void invokeVoid(int methodId, ArgumentWriter arguments) {
            calls.add("void:" + methodId);
        }

        @Override
        public CompletableFuture<Void> invokePipe(int methodId, NexusDuplexPipe pipe, PipeArgumentWriter arguments) {
            calls.add("pipe:" + methodId);
            return new CompletableFuture<>();
        }

        @Override
        public <T> CompletableFuture<Void> invokeChannel(int methodId, NexusDuplexChannel<T> channel,
                                                         PipeArgumentWriter arguments) {
            calls.add("channel:" + methodId);
            return new CompletableFuture<>();
        }

        @Override
        public <T> NexusList<T> list(int collectionId, NexusCollectionMode mode, Formatter<T> itemFormatter) {
            calls.add("list:" + collectionId + ":" + mode);
            return null;
        }
    }
}
