/**
 * Client configuration. A plain object: common options plus exactly one transport property.
 * Durations are milliseconds. Defaults match C# `ConfigBase`/`ClientConfig`.
 * Adding an optional property is not a contract change; renaming or removing one is.
 * @module
 */

/**
 * TLS options for the Node transports (`wss:`, `https:` HttpSocket, `tcpTls`). They follow `tls.connect`.
 * Browsers ignore them: the platform does the TLS (no client certificate, custom CA or pinning).
 *
 * @typedef {object} TlsOptions
 * @property {string | Uint8Array | Array<string | Uint8Array>} [ca] Trusted CA certificates (PEM or DER).
 * @property {string | Uint8Array} [cert] Client certificate chain (PEM).
 * @property {string | Uint8Array} [key] Client private key (PEM).
 * @property {Uint8Array} [pfx] Client certificate and key (PKCS#12).
 * @property {string} [passphrase] Passphrase for `key` or `pfx`.
 * @property {string} [servername] SNI and hostname-verification name. Default: the URL or `host` name.
 * @property {boolean} [rejectUnauthorized] Default true.
 * @property {(hostname: string, certificate: unknown) => Error | undefined} [checkServerIdentity] Extra server check.
 *   Node calls it only when the chain is trusted.
 * @property {Array<string | Uint8Array>} [pins] Certificate pins: SHA-256 values (base64 text or 32 bytes). A pin
 *   matches a server chain certificate when it equals the SHA-256 of the certificate's DER encoding or of its DER
 *   SubjectPublicKeyInfo (the HPKP `pin-sha256` form). With pins set, the connect succeeds only when some pin
 *   matches some chain certificate, in addition to the CA check; with `rejectUnauthorized: false` the pins alone
 *   decide. A mismatch fails the connect with `AuthenticationError`. Empty or unset: no pinning.
 */

/**
 * WebSocket transport (`ws:` or `wss:`); browser and Node. Binary messages only, no subprotocol.
 *
 * @typedef {object} WebSocketTransportConfig
 * @property {string | URL} url
 * @property {Record<string, string>} [headers] Extra upgrade request headers. Node only.
 * @property {TlsOptions} [tls] Node only.
 */

/**
 * HttpSocket transport (`http:` or `https:` upgrade to `nexnet-httpsockets`). Node only.
 *
 * @typedef {object} HttpSocketTransportConfig
 * @property {string | URL} url
 * @property {Record<string, string>} [headers] Extra upgrade request headers.
 * @property {TlsOptions} [tls]
 */

/**
 * Plain TCP transport. Node only.
 *
 * @typedef {object} TcpTransportConfig
 * @property {string} host
 * @property {number} port
 */

/**
 * TCP + TLS transport. Node only.
 *
 * @typedef {object} TcpTlsTransportConfig
 * @property {string} host
 * @property {number} port
 * @property {TlsOptions} [tls]
 */

/**
 * Unix domain socket transport. Node only.
 *
 * @typedef {object} UdsTransportConfig
 * @property {string} path
 */

/**
 * The transport property names of {@link ClientConfig}.
 *
 * @typedef {'webSocket' | 'httpSocket' | 'tcp' | 'tcpTls' | 'uds'} TransportKind
 */

/** Transport property names, in a fixed order. @type {readonly TransportKind[]} */
export const TransportKinds = Object.freeze(['webSocket', 'httpSocket', 'tcp', 'tcpTls', 'uds']);

/**
 * Reconnection policy. Mirrors C# `IReconnectionPolicy`.
 *
 * @typedef {object} ReconnectionPolicy
 * @property {(attempt: number) => number | null} reconnectDelay Delay before attempt `attempt` (0-based), or null to stop.
 * @property {(reason: import('./enums.js').DisconnectReasonValue) => boolean} [shouldReconnect] Default: SocketError, Timeout and ServerRestarting.
 */

/**
 * Log levels. Mirrors C# `NexusLogLevel` ordering.
 *
 * @typedef {'trace' | 'debug' | 'information' | 'warning' | 'error' | 'critical'} NexusLogLevel
 */

/**
 * Logger sink. J10 adds the console and rolling loggers. Token bytes are never logged.
 *
 * @typedef {object} NexusLogger
 * @property {(level: NexusLogLevel, message: string, error?: unknown) => void} log
 * @property {(path: string) => NexusLogger} [child] A logger for a sub-path (`'Session 3'`, `'Pipe 7'`).
 */

/**
 * Supplies the greeting authentication token (D15). Called before the transport connects, on every connect and
 * reconnect, bounded by `handshakeTimeout`. Return null for no token. If it throws or rejects, the connect fails
 * with `DisconnectReason.Authentication` and no connection is opened.
 *
 * @callback AuthenticateCallback
 * @param {{ signal: AbortSignal }} options
 * @returns {Uint8Array | null | undefined | Promise<Uint8Array | null | undefined>}
 */

/**
 * Client configuration. Set exactly one of `webSocket`, `httpSocket`, `tcp`, `tcpTls` and `uds`.
 * The browser entry supports only `webSocket`.
 *
 * @typedef {object} ClientConfig
 * @property {WebSocketTransportConfig} [webSocket]
 * @property {HttpSocketTransportConfig} [httpSocket]
 * @property {TcpTransportConfig} [tcp]
 * @property {TcpTlsTransportConfig} [tcpTls]
 * @property {UdsTransportConfig} [uds]
 * @property {AuthenticateCallback | null} [authenticate]
 * @property {ReconnectionPolicy | null} [reconnectionPolicy] Default: none in Node, the default policy in browsers.
 * @property {NexusLogger | null} [logger]
 * @property {number} [connectionTimeout] Default 50,000.
 * @property {number} [handshakeTimeout]
 * @property {number} [timeout] Receive timeout.
 * @property {number} [pingInterval] Milliseconds between pings; must be a positive integer. Default: a third of the
 *   server's advertised `idleTimeoutMs`, at least 1,000, or 10,000 when the server advertises none. A value set here
 *   is used as is; if it is not below the advertised idle timeout, the client logs a warning on connect.
 * @property {number} [disconnectDelay]
 * @property {number} [maxConcurrentConnectionInvocations]
 * @property {number} [maxSeparatePoolInvocations]
 * @property {number} [invocationQueueWarningThreshold]
 * @property {number} [sessionBudget]
 * @property {number} [maxPipes]
 * @property {number} [pipeWindow]
 * @property {number} [maxMessageSize]
 * @property {number} [invocationCredits]
 * @property {number} [progressTimeout]
 * @property {import('./enums.js').RemoteExceptionDetailValue} [remoteExceptionDetail]
 * @property {import('./codec.js').SerializerOptions} [serializerOptions]
 * @property {import('../session/clock.js').Clock} [clock] Time source of the client's timers (C# `ConfigBase.Time`).
 *   Default: `performance.now()` and the platform timers. Tests inject a manual clock.
 */
