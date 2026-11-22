/**
 * HTTP/1.1 upgrade request shared by the HttpSocket and WebSocket transports. Node only.
 * @module
 */
export type UpgradeResult = {
    /**
     * The `101` response.
     */
    response: import('node:http').IncomingMessage;
    /**
     * The upgraded connection (a `TLSSocket` for `https:`/`wss:`).
     */
    socket: import('node:net').Socket;
    /**
     * Bytes received after the response headers; they belong to the upgraded stream.
     */
    head: Buffer;
};
/**
 * @typedef {object} UpgradeResult
 * @property {import('node:http').IncomingMessage} response The `101` response.
 * @property {import('node:net').Socket} socket The upgraded connection (a `TLSSocket` for `https:`/`wss:`).
 * @property {Buffer} head Bytes received after the response headers; they belong to the upgraded stream.
 */
/**
 * Sends `GET <path>` with `Connection: Upgrade` on a fresh connection (no agent, no pooling) and resolves on the
 * `101`. Any other status rejects with the mapped `NexusTransportError` (`upgradeRefused`). A `3xx` is a redirect,
 * which is never followed: it rejects with `ProtocolError` and no second request is made (protocol spec §13.6/§13.7),
 * so the `Authorization` header and any query token never reach another URL. Over TLS it offers
 * ALPN `http/1.1` only (§13.6), so the server cannot pick HTTP/2. With `tls.pins` set, the request (and so its
 * headers) is sent only after the handshake and a matching pin; a mismatch rejects with `AuthenticationError`.
 *
 * @param {object} init
 * @param {'HttpSocket' | 'WebSocket'} init.transport For messages and status mapping.
 * @param {URL} init.url `http:`/`https:` (`ws:`/`wss:` are mapped by the caller).
 * @param {boolean} init.secure
 * @param {Record<string, string>} init.headers Upgrade headers (`Upgrade`, `Connection`, …) and the user's headers.
 * @param {import('../../contract/config.js').QueryToken | null} [init.queryToken] Appended to the request target only;
 *   messages show `url` without it.
 * @param {import('../../contract/config.js').TlsOptions} [init.tls]
 * @param {AbortSignal} init.signal
 * @returns {Promise<UpgradeResult>}
 */
export declare function httpUpgrade({ transport, url, secure, headers, queryToken, tls, signal }: {
    transport: 'HttpSocket' | 'WebSocket';
    url: URL;
    secure: boolean;
    headers: Record<string, string>;
    queryToken?: import('../../contract/config.js').QueryToken | null;
    tls?: import('../../contract/config.js').TlsOptions;
    signal: AbortSignal;
}): Promise<UpgradeResult>;
/**
 * True when the comma-separated header value contains `token` (case-insensitive).
 * @param {string | string[] | undefined} value
 * @param {string} token
 * @returns {boolean}
 */
export declare function headerHasToken(value: string | string[] | undefined, token: string): boolean;
