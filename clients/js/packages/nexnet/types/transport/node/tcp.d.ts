/**
 * TCP, TLS-over-TCP and Unix domain socket transports (protocol spec §13.2–§13.4). Node only.
 * @module
 */
/**
 * Opens a plain TCP transport (`ClientConfig.tcp`). Sets `TCP_NODELAY` (§13.2).
 * @param {import('../../contract/config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<import('../ITransport.js').Transport>} Satisfies {@link import('../ITransport.js').TransportFactory}.
 */
export declare function tcpTransport(config: import('../../contract/config.js').ClientConfig, { signal }: {
    signal: AbortSignal;
}): Promise<import('../ITransport.js').Transport>;
/**
 * Opens a TLS-over-TCP transport (`ClientConfig.tcpTls`). No ALPN (§13.3). A certificate or handshake failure
 * rejects with `transportError` `AuthenticationError` (`DisconnectReason.Authentication`), and so does a chain that
 * matches none of `tls.pins` (checked after the handshake, before the transport is returned).
 * @param {import('../../contract/config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<import('../ITransport.js').Transport>} Satisfies {@link import('../ITransport.js').TransportFactory}.
 */
export declare function tcpTlsTransport(config: import('../../contract/config.js').ClientConfig, { signal }: {
    signal: AbortSignal;
}): Promise<import('../ITransport.js').Transport>;
/**
 * Opens a Unix domain socket transport (`ClientConfig.uds`, §13.4). On Windows, Node connects `path` as a named
 * pipe, not as an `AF_UNIX` socket.
 * @param {import('../../contract/config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<import('../ITransport.js').Transport>} Satisfies {@link import('../ITransport.js').TransportFactory}.
 */
export declare function udsTransport(config: import('../../contract/config.js').ClientConfig, { signal }: {
    signal: AbortSignal;
}): Promise<import('../ITransport.js').Transport>;
