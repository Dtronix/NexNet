/**
 * Validates a {@link import('../contract/config.js').ClientConfig} and resolves its defaults (C# `ConfigBase` and
 * `ClientConfig`). Durations are milliseconds.
 * @module
 */
export type ResolvedClientOptions = {
    /**
     * The configured transport property.
     */
    kind: import('../contract/config.js').TransportKind;
    /**
     * Transport name for logs and the plaintext warning (`tcp`, `ws://`, …).
     */
    transportName: string;
    /**
     * True for TLS, WSS, HTTPS and UDS (D7, F05).
     */
    isSecure: boolean;
    clock: import('./clock.js').Clock;
    logger: import('../contract/config.js').NexusLogger | null;
    authenticate: import('../contract/config.js').AuthenticateCallback | null;
    reconnectionPolicy: import('../contract/config.js').ReconnectionPolicy | null;
    connectionTimeout: number;
    handshakeTimeout: number;
    timeout: number;
    /**
     * Null: a third of the server's `idleTimeoutMs` (at least 1,000), else 10,000.
     */
    pingInterval: number | null;
    disconnectDelay: number;
    maxConcurrentConnectionInvocations: number;
    maxSeparatePoolInvocations: number;
    invocationQueueWarningThreshold: number;
    sessionBudget: number;
    maxPipes: number;
    pipeWindow: number;
    maxMessageSize: number;
    invocationCredits: number;
    progressTimeout: number;
    remoteExceptionDetail: import('../contract/enums.js').RemoteExceptionDetailValue;
    /**
     * The configured options; an unset (or 0)
     * `maxElements` is replaced by {@link defaultMaxElements} of `maxMessageSize` (C# `ConfigBase.SerializerOptions`).
     */
    serializerOptions: import('../contract/codec.js').SerializerOptions;
};
/**
 * @typedef {object} ResolvedClientOptions
 * @property {import('../contract/config.js').TransportKind} kind The configured transport property.
 * @property {string} transportName Transport name for logs and the plaintext warning (`tcp`, `ws://`, …).
 * @property {boolean} isSecure True for TLS, WSS, HTTPS and UDS (D7, F05).
 * @property {import('./clock.js').Clock} clock
 * @property {import('../contract/config.js').NexusLogger | null} logger
 * @property {import('../contract/config.js').AuthenticateCallback | null} authenticate
 * @property {import('../contract/config.js').ReconnectionPolicy | null} reconnectionPolicy
 * @property {number} connectionTimeout
 * @property {number} handshakeTimeout
 * @property {number} timeout
 * @property {number | null} pingInterval Null: a third of the server's `idleTimeoutMs` (at least 1,000), else 10,000.
 * @property {number} disconnectDelay
 * @property {number} maxConcurrentConnectionInvocations
 * @property {number} maxSeparatePoolInvocations
 * @property {number} invocationQueueWarningThreshold
 * @property {number} sessionBudget
 * @property {number} maxPipes
 * @property {number} pipeWindow
 * @property {number} maxMessageSize
 * @property {number} invocationCredits
 * @property {number} progressTimeout
 * @property {import('../contract/enums.js').RemoteExceptionDetailValue} remoteExceptionDetail
 * @property {import('../contract/codec.js').SerializerOptions} serializerOptions The configured options; an unset (or 0)
 *   `maxElements` is replaced by {@link defaultMaxElements} of `maxMessageSize` (C# `ConfigBase.SerializerOptions`).
 */
/** Ping interval when neither the config nor the server sets one (C# `ClientConfig.DefaultPingInterval`). */
export declare const DEFAULT_PING_INTERVAL = 10000;
/** Shortest ping interval derived from the server's advertised idle timeout (C# `ClientConfig.MinimumDerivedPingInterval`). */
export declare const MIN_DERIVED_PING_INTERVAL = 1000;
/**
 * The ping interval for a server that advertised `idleTimeoutMs` (S04; C# `ClientConfig.GetEffectivePingInterval`).
 * An explicit interval is used as is. Otherwise a third of the advertised idle timeout, at least
 * {@link MIN_DERIVED_PING_INTERVAL}; {@link DEFAULT_PING_INTERVAL} when the server advertised none (null or 0).
 * @param {number | null} configured The resolved `pingInterval` option; null to derive it.
 * @param {number | null} idleTimeoutMs The advertised idle timeout (uint32), or null/0 when none.
 * @returns {number}
 */
export declare function effectivePingInterval(configured: number | null, idleTimeoutMs: number | null): number;
/** Smallest default `maxElements` (protocol specification §11). */
export declare const MIN_DEFAULT_MAX_ELEMENTS = 65536;
/**
 * The reader's `maxElements` when the config leaves it unset: `max(65536, maxMessageSize / 16)` (spec §11; C#
 * `ConfigBase.SerializerOptions`).
 * @param {number} maxMessageSize
 * @returns {number}
 */
export declare function defaultMaxElements(maxMessageSize: number): number;
/**
 * The serializer options the session's readers use: `configured`, with an unset (undefined, null or 0)
 * `maxElements` filled in from `maxMessageSize`. As in C#, 0 means "use the default", not "no limit".
 * @param {import('../contract/codec.js').SerializerOptions | null | undefined} configured
 * @param {number} maxMessageSize
 * @returns {import('../contract/codec.js').SerializerOptions}
 */
export declare function sessionSerializerOptions(configured: import('../contract/codec.js').SerializerOptions | null | undefined, maxMessageSize: number): import('../contract/codec.js').SerializerOptions;
/**
 * Name and security of the configured transport, known before it connects (as C# `TransportName`/`IsSecureTransport`).
 * @param {import('../contract/config.js').TransportKind} kind
 * @param {any} transportConfig
 * @returns {{ name: string, isSecure: boolean }}
 */
export declare function describeTransport(kind: import('../contract/config.js').TransportKind, transportConfig: any): {
    name: string;
    isSecure: boolean;
};
/**
 * Validates `config` and returns it with defaults applied.
 *
 * @param {import('../contract/config.js').ClientConfig} config
 * @param {Readonly<Partial<Record<import('../contract/config.js').TransportKind, unknown>>>} supported Transport factories of the entry point.
 * @returns {ResolvedClientOptions}
 * @throws {TypeError} No transport, several transports, an unsupported transport, or a malformed option.
 * @throws {RangeError} An option out of range, or a session budget that can't hold the fixed reservations.
 */
export declare function resolveClientOptions(config: import('../contract/config.js').ClientConfig, supported: Readonly<Partial<Record<import('../contract/config.js').TransportKind, unknown>>>): ResolvedClientOptions;
