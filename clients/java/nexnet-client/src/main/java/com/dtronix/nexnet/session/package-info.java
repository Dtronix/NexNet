/**
 * The session core: framing, handshake, ping and timeouts, disconnect handling, and the reader and writer threads of
 * one connection. Runtime-internal; applications use {@code com.dtronix.nexnet.client.NexusClient}.
 *
 * <p>Attachment points for the rest of the runtime:
 * <ul>
 *   <li>Transports implement {@link com.dtronix.nexnet.transport.Transport} and are opened by a
 *       {@link com.dtronix.nexnet.transport.Transport.Connector} on a runtime thread.</li>
 *   <li>Received messages other than {@code Ping}, disconnects and {@code ServerGreeting} go to the session's
 *       {@link com.dtronix.nexnet.session.MessageHandler}, on the reader thread.</li>
 *   <li>Outbound messages are queued with {@link com.dtronix.nexnet.session.NexusSession#send(Frame)}; small control
 *       messages (credit, cancellation, aborts, pipe control) go through the coalescing
 *       {@link com.dtronix.nexnet.session.ControlSender}.</li>
 *   <li>Timers use {@link com.dtronix.nexnet.session.SessionTimers}, which tests replace with a manual clock.</li>
 *   <li>{@link com.dtronix.nexnet.session.SessionCounters} reports open sessions, live threads, active timers and
 *       pending writes for leak audits.</li>
 * </ul>
 */
package com.dtronix.nexnet.session;
