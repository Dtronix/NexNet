package com.dtronix.nexnet.serialization;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.NexusDateTime;
import com.dtronix.nexnet.NexusDateTimeKind;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.Tuple2;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.pipes.NexusChannelReader;
import com.dtronix.nexnet.pipes.NexusChannelWriter;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.Frame;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.ProxyBase;
import com.dtronix.nexnet.spi.ProxyInvoker;
import java.util.Arrays;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

/**
 * The MessagePack codec plugged into a real {@link NexusClient} (session, invocation and pipe runtimes) against a
 * scripted server: arguments, results and channel items are real MessagePack, written the way generated code
 * (K03/K04) will write them.
 */
@Timeout(30)
class CodecEndToEndTest {
    private static final int CLIENT_HASH = -987654321;
    private static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor(NexusRuntime.VERSION, "v2", 123456789, CLIENT_HASH, new int[] {0});
    private static final Formatter<Tuple2<Integer, String>> ITEM = Formatters.tuple(Formatters.INT, Formatters.STRING);

    /** A hand-written stand-in for a generated proxy. */
    static final class Proxy extends ProxyBase {
        Proxy(ProxyInvoker invoker) {
            super(invoker);
        }

        // C# ValueTask<int> Add(int a, int b)
        CompletableFuture<Integer> add(int a, int b) {
            return invoke(3, w -> {
                w.writeArrayHeader(2);
                Formatters.INT.write(w, a);
                Formatters.INT.write(w, b);
            }, Formatters.INT, CancellationToken.NONE);
        }

        // C# ValueTask<List<string>> Names(DateTime since)
        CompletableFuture<List<String>> names(NexusDateTime since) {
            return invoke(4, w -> {
                w.writeArrayHeader(1);
                Formatters.DATE_TIME.write(w, since);
            }, Formatters.list(Formatters.STRING), CancellationToken.NONE);
        }

        // C# ValueTask Stream(INexusDuplexChannel<(int, string)> channel)
        CompletableFuture<Void> stream(NexusDuplexChannel<Tuple2<Integer, String>> channel) {
            return invokeChannel(5, channel, (w, pipeId) -> {
                w.writeArrayHeader(1);
                w.writeInt(pipeId);
            });
        }
    }

    /** A hand-written stand-in for a generated client nexus: method 0 is {@code string Describe(int, string)}. */
    static final class Nexus extends ClientNexusBase<Proxy> {
        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader args, InvocationContext ctx) {
            if (methodId != 0) {
                throw new IllegalArgumentException("Unknown method id " + methodId);
            }
            args.readArrayHeader();
            int number = Formatters.INT.read(args);
            String text = Formatters.STRING.read(args);
            return ctx.result(CompletableFuture.completedFuture(text + number), Formatters.STRING);
        }
    }

    /** One frame from the client. */
    static final class Received {
        final int type;
        final byte[] post;
        final byte[] body;

        Received(int type, byte[] post, byte[] body) {
            this.type = type;
            this.post = post;
            this.body = body;
        }
    }

    private final List<LoopbackTransport> transports = new CopyOnWriteArrayList<>();
    private NexusClient<Proxy> client;
    private FakeServer server;

    @AfterEach
    void tearDown() {
        if (client != null) {
            client.close();
        }
    }

    private void connect() throws Exception {
        client = NexusClient.create(ClientConfig.tcp("h", 1).build(), HANDSHAKE, new Nexus(), Proxy::new,
            (config, timeout) -> {
                LoopbackTransport transport = new LoopbackTransport();
                transports.add(transport);
                return transport;
            });
        CompletableFuture<Void> connected = client.connect();
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (transports.isEmpty() && System.nanoTime() < deadline) {
            Thread.sleep(5);
        }
        server = transports.get(0).server();
        server.readHandshake();
        server.write(FakeServer.PREAMBLE);
        server.write(FakeServer.serverGreeting(CLIENT_HASH, 1, 4 * 1024 * 1024, 32));
        connected.get(5, TimeUnit.SECONDS);
    }

    /** Reads frames until one of {@code type}, skipping pings, credits and window updates. */
    private Received expect(int type) {
        while (true) {
            int t = server.readByte();
            if (t == MessageTypes.PING || (t >= MessageTypes.DISCONNECT_FIRST && t <= MessageTypes.DISCONNECT_LAST)) {
                assertTrue(t != type || t == MessageTypes.PING, "disconnected: " + t);
                continue;
            }
            byte[] length = server.read(2);
            int bodyLength = (length[0] & 0xFF) | ((length[1] & 0xFF) << 8);
            int postLength = t == MessageTypes.DUPLEX_PIPE_WRITE || t == MessageTypes.PIPE_WINDOW_UPDATE ? 2
                : t == MessageTypes.MESSAGE_CHUNK ? 3 : 0;
            byte[] post = server.read(postLength);
            byte[] body = server.read(bodyLength);
            if (t == type) {
                return new Received(t, post, body);
            }
        }
    }

    private static byte[] body(Object... fields) {
        MsgPackWriter w = new MsgPackWriter();
        w.writeArrayHeader(fields.length);
        for (Object field : fields) {
            if (field instanceof byte[]) {
                w.writeRaw((byte[]) field);
            } else {
                w.writeInt(((Number) field).longValue());
            }
        }
        return w.toByteArray();
    }

    private static Object[] parseInvocation(byte[] body) {
        MsgPackReader r = new MsgPackReader(body);
        r.readArrayHeader(4);
        Object[] fields = {r.readUInt16(), r.readUInt16(), r.readUInt8(), r.readRawValue()};
        r.ensureEnd();
        return fields;
    }

    @Test
    void proxyCallsCarryRealArgumentsAndResults() throws Exception {
        connect();
        CompletableFuture<Integer> sum = client.proxy().add(2, 40);
        Object[] invocation = parseInvocation(expect(MessageTypes.INVOCATION).body);
        assertEquals(3, invocation[1]);
        assertEquals(0, invocation[2], "flags");
        assertEquals(Tuple2.of(2, 40), Formatters.deserialize(Formatters.tuple(Formatters.INT, Formatters.INT),
            (byte[]) invocation[3]));
        server.write(Frame.of(MessageTypes.INVOCATION_RESULT,
            body(invocation[0], 1, Formatters.serialize(Formatters.INT, 42))).bytes());
        assertEquals(42, sum.get(5, TimeUnit.SECONDS));

        NexusDateTime since = NexusDateTime.fromEpochMillisUtc(1_700_000_000_000L);
        CompletableFuture<List<String>> names = client.proxy().names(since);
        invocation = parseInvocation(expect(MessageTypes.INVOCATION).body);
        MsgPackReader args = new MsgPackReader((byte[]) invocation[3]);
        args.readArrayHeader(1);
        assertEquals(since, Formatters.DATE_TIME.read(args));
        assertEquals(NexusDateTimeKind.UTC, since.kind());
        List<String> result = Arrays.asList("ann", null, "zoë");
        server.write(Frame.of(MessageTypes.INVOCATION_RESULT,
            body(invocation[0], 1, Formatters.serialize(Formatters.list(Formatters.STRING), result))).bytes());
        assertEquals(result, names.get(5, TimeUnit.SECONDS));
    }

    @Test
    void serverInvocationsReadArgumentsAndReturnResults() throws Exception {
        connect();
        MsgPackWriter arguments = new MsgPackWriter();
        arguments.writeArrayHeader(2);
        Formatters.INT.write(arguments, 21);
        Formatters.STRING.write(arguments, "x");
        server.write(Frame.of(MessageTypes.INVOCATION, body(7, 0, 0, arguments.toByteArray())).bytes());
        MsgPackReader r = new MsgPackReader(expect(MessageTypes.INVOCATION_RESULT).body);
        assertEquals(3, r.readArrayHeader());
        assertEquals(7, r.readUInt16());
        assertEquals(1, r.readUInt8(), "completed with a result");
        assertEquals("x21", Formatters.STRING.read(r));
        r.ensureEnd();
    }

    @Test
    void channelItemsAreRealMessagePackBothWays() throws Exception {
        connect();
        NexusDuplexChannel<Tuple2<Integer, String>> channel = client.createChannel(ITEM);
        CompletableFuture<Void> sent = client.proxy().stream(channel);
        Object[] invocation = parseInvocation(expect(MessageTypes.INVOCATION).body);
        assertEquals(5, invocation[1]);
        assertEquals(3, invocation[2], "IgnoreReturn | DuplexPipe");
        MsgPackReader args = new MsgPackReader((byte[]) invocation[3]);
        args.readArrayHeader(1);
        int clientPipeId = args.readUInt8();
        int serverPipeId = 9;
        int fullId = clientPipeId | (serverPipeId << 8);
        sent.get(5, TimeUnit.SECONDS);
        server.write(Frame.of(MessageTypes.DUPLEX_PIPE_UPDATE_STATE, body(fullId, 4, 65536)).bytes());

        NexusChannelWriter<Tuple2<Integer, String>> writer = channel.writer().get(5, TimeUnit.SECONDS);
        writer.writeAll(Arrays.asList(Tuple2.of(1, "one"), Tuple2.of(2, null))).get(5, TimeUnit.SECONDS);
        Received data = expect(MessageTypes.DUPLEX_PIPE_WRITE);
        assertEquals(clientPipeId, data.post[0] & 0xFF);
        assertEquals(serverPipeId, data.post[1] & 0xFF);
        // Each item is its length (LEB128; one byte here) followed by exactly one MessagePack value.
        int at = 0;
        for (Tuple2<Integer, String> expected : Arrays.asList(Tuple2.of(1, "one"), Tuple2.of(2, (String) null))) {
            int length = data.body[at++];
            assertEquals(expected, Formatters.deserialize(ITEM, Arrays.copyOfRange(data.body, at, at + length)));
            at += length;
        }
        assertEquals(data.body.length, at);

        byte[] item = Formatters.serialize(ITEM, Tuple2.of(-5, "from server"));
        byte[] frame = new byte[5 + 1 + item.length];
        frame[0] = (byte) MessageTypes.DUPLEX_PIPE_WRITE;
        frame[1] = (byte) (1 + item.length);
        frame[3] = (byte) clientPipeId;
        frame[4] = (byte) serverPipeId;
        frame[5] = (byte) item.length;
        System.arraycopy(item, 0, frame, 6, item.length);
        server.write(frame);
        NexusChannelReader<Tuple2<Integer, String>> reader = channel.reader().get(5, TimeUnit.SECONDS);
        assertEquals(Optional.of(Tuple2.of(-5, "from server")), reader.read().get(5, TimeUnit.SECONDS));
        channel.close();
    }
}
