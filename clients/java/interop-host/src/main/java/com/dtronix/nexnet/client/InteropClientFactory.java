package com.dtronix.nexnet.client;

import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.session.SessionTimers;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.ProxyInvoker;
import com.dtronix.nexnet.transport.NexusConnector;
import java.util.function.Function;

/**
 * Test-only access of the JVM interop host to the package-private {@code NexusClient.create} overload that takes the
 * session clock and options: fake time ({@code ClientSpec.fakeTime}), {@code maxMessageSize} and
 * {@code invocationCredits} have no public setter. Lives in the runtime's package on the host's class path only (a
 * split package of the unnamed module); never shipped.
 */
public final class InteropClientFactory {
    private InteropClientFactory() {
    }

    /**
     * Creates a client on a custom clock with custom session options.
     *
     * @param config the configuration
     * @param handshake the generated handshake constants
     * @param nexus the client nexus
     * @param proxyFactory the generated proxy constructor
     * @param timers the session clock and timer scheduler
     * @param sessionOptions the session options
     * @param <TProxy> the proxy type
     * @return the client, not connected
     */
    public static <TProxy> NexusClient<TProxy> create(ClientConfig config, HandshakeDescriptor handshake,
                                                      ClientNexusBase<TProxy> nexus,
                                                      Function<ProxyInvoker, TProxy> proxyFactory,
                                                      SessionTimers timers, SessionOptions sessionOptions) {
        return NexusClient.create(config, handshake, nexus, proxyFactory, NexusConnector.defaults(), timers,
            sessionOptions);
    }
}
