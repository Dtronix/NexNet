package com.dtronix.nexnet.android;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;
import static org.junit.Assume.assumeTrue;

import android.os.Bundle;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.ProxyBase;
import com.dtronix.nexnet.spi.ProxyInvoker;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import org.jspecify.annotations.Nullable;
import org.junit.Test;
import org.junit.runner.RunWith;

/**
 * Network smoke test against the interop server of the nightly job (src/NexNet.InteropServer/Cli), reached from the
 * emulator at 10.0.2.2. The job passes the instrumentation arguments {@code nexnet.token} and
 * {@code nexnet.endpoint.<name>} (tcp, websocket, httpsocket, ...); without them the tests are skipped.
 *
 * <p>The CLI server hosts its probe nexus, which has no Java export yet, so the client offers a handshake the server
 * does not know: a full round trip (transport, preamble, greeting, the server's disconnect code) that ends in a
 * mismatch. Invoking a method and echoing a pipe need a Java export of the server's nexus (K13 follow-up).
 */
@RunWith(AndroidJUnit4.class)
public class InteropServerSmokeTest {
    /** Hashes no server nexus has. */
    private static final HandshakeDescriptor UNKNOWN =
        new HandshakeDescriptor(NexusRuntime.VERSION, null, 0x4E784E31, 0x4E784E32, new int[0]);

    static final class Proxy extends ProxyBase {
        Proxy(ProxyInvoker invoker) {
            super(invoker);
        }
    }

    static final class Nexus extends ClientNexusBase<Proxy> {
        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            throw new IllegalArgumentException("Unknown method id " + methodId);
        }
    }

    private static @Nullable String argument(String name) {
        Bundle arguments = InstrumentationRegistry.getArguments();
        return arguments.getString(name);
    }

    private static void handshakeReachesTheServer(ClientConfig.Builder builder) throws Exception {
        String token = argument("nexnet.token");
        if (token != null) {
            byte[] bytes = token.getBytes(StandardCharsets.UTF_8);
            builder.authenticate(() -> bytes);
        }
        ClientConfig config = NexusAndroid.configure(builder).connectionTimeoutMillis(20_000).build();
        try (NexusClient<Proxy> client = NexusClient.create(config, UNKNOWN, new Nexus(), Proxy::new)) {
            ConnectionResult result = client.tryConnect().get(30, TimeUnit.SECONDS);
            assertFalse("The server must refuse the unknown nexus.", result.isSuccess());
            DisconnectReason reason = result.disconnectReason();
            assertTrue("Expected a nexus mismatch, got " + reason,
                reason == DisconnectReason.CLIENT_MISMATCH || reason == DisconnectReason.SERVER_MISMATCH);
        }
    }

    @Test
    public void tcp() throws Exception {
        String endpoint = argument("nexnet.endpoint.tcp");
        assumeTrue("No nexnet.endpoint.tcp instrumentation argument.", endpoint != null);
        URI uri = URI.create(endpoint);
        assertEquals("tcp", uri.getScheme());
        handshakeReachesTheServer(ClientConfig.tcp(uri.getHost(), uri.getPort()));
    }

    @Test
    public void webSocket() throws Exception {
        String endpoint = argument("nexnet.endpoint.websocket");
        assumeTrue("No nexnet.endpoint.websocket instrumentation argument.", endpoint != null);
        handshakeReachesTheServer(ClientConfig.webSocket(URI.create(endpoint)));
    }

    @Test
    public void httpSocket() throws Exception {
        String endpoint = argument("nexnet.endpoint.httpsocket");
        assumeTrue("No nexnet.endpoint.httpsocket instrumentation argument.", endpoint != null);
        handshakeReachesTheServer(ClientConfig.httpSocket(URI.create(endpoint)));
    }
}
