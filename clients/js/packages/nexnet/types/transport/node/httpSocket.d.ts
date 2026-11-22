/**
 * HttpSocket transport: an HTTP/1.1 connection upgraded to `nexnet-httpsockets` (protocol spec §13.6). Node only.
 * @module
 */
/** The HttpSocket upgrade token. */
export declare const HTTP_SOCKET_UPGRADE = "nexnet-httpsockets";
/**
 * Opens an HttpSocket transport (`ClientConfig.httpSocket`). Bytes that arrive with the `101` are fed to the session
 * first. `401`/`403` reject with `transportError` `AuthenticationError`; a `2xx` answer, and a `3xx` redirect (never
 * followed), is `ProtocolError`; any other status is a connect failure (`InternalError`). `status` is set on each.
 *
 * @param {import('../../contract/config.js').ClientConfig} config
 * @param {{ signal: AbortSignal }} options
 * @returns {Promise<import('../ITransport.js').Transport>} Satisfies {@link import('../ITransport.js').TransportFactory}.
 */
export declare function httpSocketTransport(config: import('../../contract/config.js').ClientConfig, { signal }: {
    signal: AbortSignal;
}): Promise<import('../ITransport.js').Transport>;
