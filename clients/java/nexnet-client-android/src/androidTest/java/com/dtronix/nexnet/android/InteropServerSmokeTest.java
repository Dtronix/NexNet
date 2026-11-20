package com.dtronix.nexnet.android;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assume.assumeTrue;

import android.os.Bundle;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.testnexus.InteropAllTypes;
import com.dtronix.nexnet.testnexus.InteropClient;
import com.dtronix.nexnet.testnexus.InteropClientNexusBase;
import com.dtronix.nexnet.testnexus.InteropServerProxy;
import com.dtronix.nexnet.testnexus.InteropShape;
import java.io.ByteArrayOutputStream;
import java.net.URI;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import org.jspecify.annotations.Nullable;
import org.junit.Test;
import org.junit.runner.RunWith;

/**
 * Network smoke test against the interop server of the Android CI job (src/NexNet.InteropServer/Cli), reached from the
 * emulator at 10.0.2.2. The job passes the instrumentation arguments {@code nexnet.token} and
 * {@code nexnet.endpoint.<name>} (tcp, websocket, httpsocket, ...); without them the tests are skipped.
 *
 * <p>The server hosts the interop test nexus (src/NexNet.TestNexus), whose generated Java client this test compiles. On
 * each transport the client connects with the token, invokes methods and echoes bytes through a pipe.
 */
@RunWith(AndroidJUnit4.class)
public class InteropServerSmokeTest {
    private static final long TIMEOUT_SECONDS = 30;

    /** The identity the CI interop server's token validator gives a client with the right token. */
    private static final String IDENTITY = "cli";

    /** The client nexus. The smoke test makes no call that reaches it; each method answers as the interop host would. */
    static final class Nexus extends InteropClientNexusBase {
        @Override
        protected void clientNotify(String message) {
        }

        @Override
        protected CompletableFuture<Void> clientPing() {
            return CompletableFuture.completedFuture(null);
        }

        @Override
        protected CompletableFuture<@Nullable String> clientEchoString(@Nullable String value) {
            return CompletableFuture.completedFuture(value);
        }

        @Override
        protected CompletableFuture<@Nullable InteropAllTypes> clientEchoAllTypes(@Nullable InteropAllTypes value) {
            return CompletableFuture.completedFuture(value);
        }

        @Override
        protected CompletableFuture<@Nullable InteropShape> clientEchoShape(@Nullable InteropShape value) {
            return CompletableFuture.completedFuture(value);
        }

        @Override
        protected CompletableFuture<Integer> clientDelay(int milliseconds, int value, CancellationToken cancellationToken) {
            return CompletableFuture.completedFuture(value);
        }

        @Override
        protected CompletableFuture<Void> clientEchoPipe(NexusDuplexPipe pipe) {
            return pipe.forwardTo(pipe);
        }
    }

    private static @Nullable String argument(String name) {
        Bundle arguments = InstrumentationRegistry.getArguments();
        return arguments.getString(name);
    }

    private static <T> T await(CompletableFuture<T> future) throws Exception {
        return future.get(TIMEOUT_SECONDS, TimeUnit.SECONDS);
    }

    /** Connects, invokes and echoes a pipe; with a token, also checks the identity the server gave the session. */
    private static void invokeAndEchoAPipe(ClientConfig.Builder builder) throws Exception {
        String token = argument("nexnet.token");
        if (token != null) {
            byte[] bytes = token.getBytes(StandardCharsets.UTF_8);
            builder.authenticate(() -> bytes);
        }
        ClientConfig config = NexusAndroid.configure(builder).connectionTimeoutMillis(20_000).build();
        try (NexusClient<InteropServerProxy> client = InteropClient.create(config, new Nexus())) {
            await(client.connect());
            InteropServerProxy proxy = client.proxy();
            assertEquals(Integer.valueOf(42), await(proxy.echoInt32(42)));
            assertEquals("android smoke", await(proxy.echoString("android smoke")));
            if (token != null) {
                assertEquals(IDENTITY, await(proxy.getIdentity()));
            }
            echoPipe(client, proxy);
            await(client.disconnect());
        }
    }

    /**
     * Sends more bytes than one pipe window through the server's EchoPipe and reads them back. Reading runs while
     * writing, because the server only echoes what it can write back within the client's credit.
     */
    private static void echoPipe(NexusClient<InteropServerProxy> client, InteropServerProxy proxy) throws Exception {
        byte[] sent = new byte[256 * 1024];
        for (int i = 0; i < sent.length; i++) {
            sent[i] = (byte) (i * 31 + 7);
        }
        NexusDuplexPipe pipe = client.createPipe();
        try {
            CompletableFuture<Void> call = proxy.echoPipe(pipe);
            await(pipe.ready());
            CompletableFuture<byte[]> received = readAll(pipe);
            int chunk = 16 * 1024;
            for (int offset = 0; offset < sent.length; offset += chunk) {
                await(pipe.write(ByteBuffer.wrap(sent, offset, Math.min(chunk, sent.length - offset))));
            }
            await(pipe.completeOutput());
            assertArrayEquals(sent, await(received));
            await(call);
        } finally {
            pipe.close();
        }
    }

    /** Reads the pipe until the peer completes its output. */
    private static CompletableFuture<byte[]> readAll(NexusDuplexPipe pipe) {
        CompletableFuture<byte[]> done = new CompletableFuture<>();
        readNext(pipe, new ByteArrayOutputStream(), done);
        return done;
    }

    private static void readNext(NexusDuplexPipe pipe, ByteArrayOutputStream output, CompletableFuture<byte[]> done) {
        pipe.read().whenComplete((buffer, error) -> {
            if (error != null) {
                done.completeExceptionally(error);
            } else if (buffer == null) {
                done.complete(output.toByteArray());
            } else {
                byte[] bytes = new byte[buffer.remaining()];
                buffer.get(bytes);
                output.write(bytes, 0, bytes.length);
                readNext(pipe, output, done);
            }
        });
    }

    @Test
    public void tcp() throws Exception {
        String endpoint = argument("nexnet.endpoint.tcp");
        assumeTrue("No nexnet.endpoint.tcp instrumentation argument.", endpoint != null);
        URI uri = URI.create(endpoint);
        assertEquals("tcp", uri.getScheme());
        invokeAndEchoAPipe(ClientConfig.tcp(uri.getHost(), uri.getPort()));
    }

    @Test
    public void webSocket() throws Exception {
        String endpoint = argument("nexnet.endpoint.websocket");
        assumeTrue("No nexnet.endpoint.websocket instrumentation argument.", endpoint != null);
        invokeAndEchoAPipe(ClientConfig.webSocket(URI.create(endpoint)));
    }

    @Test
    public void httpSocket() throws Exception {
        String endpoint = argument("nexnet.endpoint.httpsocket");
        assumeTrue("No nexnet.endpoint.httpsocket instrumentation argument.", endpoint != null);
        invokeAndEchoAPipe(ClientConfig.httpSocket(URI.create(endpoint)));
    }
}
