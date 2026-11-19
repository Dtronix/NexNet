/**
 * Node transport helpers (J06): TLS options, connect-error mapping and abort handling. Node only.
 * @module
 */
/**
 * Builds `tls.connect` / `https.request` options from {@link import('../../contract/config.js').TlsOptions}.
 * The host name is verified (Node's `tls.checkServerIdentity`) unless `rejectUnauthorized` is false; a user
 * `checkServerIdentity` runs in addition to it, after it passes (pinning). SNI is the configured `servername`, else
 * the host when it is a DNS name (never an IP literal, RFC 6066). `pins` are not part of the result: the transports
 * check them after the handshake with {@link checkPins}.
 *
 * @param {import('../../contract/config.js').TlsOptions | undefined} options
 * @param {string} host
 * @returns {import('node:tls').ConnectionOptions}
 */
export declare function tlsConnectOptions(options: import('../../contract/config.js').TlsOptions | undefined, host: string): import('node:tls').ConnectionOptions;
/**
 * Decodes `TlsOptions.pins` into 32-byte SHA-256 values. Each pin is base64 (or base64url) text, or the raw bytes.
 *
 * @param {import('../../contract/config.js').TlsOptions | undefined} options
 * @returns {Buffer[]} Empty when no pin is set.
 * @throws {TypeError} A pin is not 32 bytes of SHA-256 in base64 or a `Uint8Array`.
 */
export declare function tlsPins(options: import('../../contract/config.js').TlsOptions | undefined): Buffer[];
/**
 * Checks the server chain of a connected TLS socket against the pins (Java `TlsOptions` parity): a pin matches a
 * chain certificate when it equals the SHA-256 of the certificate's DER encoding or of its DER
 * SubjectPublicKeyInfo. The connection is accepted when any pin matches any certificate of the chain the server
 * sent (leaf, intermediates and, when Node can find it, the root). Runs after the handshake, so it applies with
 * `rejectUnauthorized: false` too.
 *
 * @param {import('node:tls').TLSSocket} socket
 * @param {Buffer[]} pins From {@link tlsPins}. Empty accepts every chain.
 * @returns {Error | undefined} An error with code `ERR_TLS_CERT_PIN_MISMATCH` when no pin matches.
 */
export declare function checkPins(socket: import('node:tls').TLSSocket, pins: Buffer[]): Error | undefined;
/**
 * True when `error` is a TLS handshake or certificate failure.
 * @param {unknown} error
 * @returns {boolean}
 */
export declare function isTlsError(error: unknown): boolean;
/**
 * Maps a socket or TLS connect error to a `NexusTransportError` (protocol spec §13.1).
 *
 * @param {string} what Transport and address, for the message.
 * @param {unknown} error
 * @param {{ tlsPhase?: boolean }} [state] `tlsPhase`: the TCP connect succeeded and the TLS handshake had not.
 * @returns {import('../TransportBase.js').TransportConnectError}
 */
export declare function connectError(what: string, error: unknown, state?: {
    tlsPhase?: boolean;
}): import('../TransportBase.js').TransportConnectError;
/**
 * Runs a connect attempt that can be aborted. `start` gets `resolve`/`reject` and returns a cleanup function that
 * destroys whatever it opened; cleanup runs when the attempt fails or is aborted, never after success.
 *
 * @template T
 * @param {AbortSignal} signal
 * @param {(resolve: (value: T) => void, reject: (error: unknown) => void) => (error?: unknown) => void} start
 * @returns {Promise<T>}
 */
export declare function abortableConnect<T>(signal: AbortSignal, start: (resolve: (value: T) => void, reject: (error: unknown) => void) => (error?: unknown) => void): Promise<T>;
/**
 * The host of a URL without IPv6 brackets.
 * @param {URL} url
 * @returns {string}
 */
export declare function urlHost(url: URL): string;
