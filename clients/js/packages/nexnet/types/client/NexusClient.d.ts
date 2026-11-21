/**
 * The client: connect, handshake, state machine, events, plaintext warning and disconnect.
 * Reconnection, pooling and full snapshots live in their own modules. Invocations and pipes attach to each session before it
 * starts (`attachSessionLayers`).
 * @module
 */
import { NexusSession } from '../session/NexusSession.js';
import { ReconnectController } from '../reconnect/ReconnectController.js';
export type ConnectionResult = {
    success: boolean;
    state: import('../contract/enums.js').ConnectionResultStateValue;
    /**
     * `None` on success.
     */
    disconnectReason: import('../contract/enums.js').DisconnectReasonValue;
    /**
     * The error when `state` is `Exception`.
     */
    error?: unknown;
};
export type StateChangeDetail = {
    state: import('../contract/enums.js').ConnectionStateValue;
    previous: import('../contract/enums.js').ConnectionStateValue;
};
export type DisconnectedDetail = {
    reason: import('../contract/enums.js').DisconnectReasonValue;
};
export type NexusClientEventMap = {
    statechange: CustomEvent<StateChangeDetail>;
    disconnected: CustomEvent<DisconnectedDetail>;
};
export type ClientSessionHandle = {
    readonly client: NexusClient<any, any>;
    readonly session: NexusSession | null;
};
/**
 * A NexNet client: one session to one server. Dispatches `statechange` and `disconnected` events (`CustomEvent`
 * with {@link StateChangeDetail} and {@link DisconnectedDetail}), always from a microtask, never re-entrantly.
 * Create it with the generated `create<Export>Client(config, nexus)`, which calls {@link NexusClient.create}.
 *
 * @template {import('../contract/nexus.js').ClientNexusBase<TProxy>} TNexus
 * @template {import('../contract/proxy.js').ProxyBase} TProxy
 */
export declare class NexusClient<TNexus extends import('../contract/nexus.js').ClientNexusBase<TProxy>, TProxy extends import('../contract/proxy.js').ProxyBase> extends EventTarget {
    #private;
    /**
     * @readonly
     * @type {import('../contract/config.js').ClientConfig}
     */
    readonly config: import('../contract/config.js').ClientConfig;
    /**
     * @readonly
     * @type {TNexus}
     */
    readonly nexus: TNexus;
    /**
     * @protected
     * @readonly
     * @type {import('../contract/handshake.js').ClientBinding<TProxy>}
     */
    protected readonly _binding: import('../contract/handshake.js').ClientBinding<TProxy>;
    /**
     * Transport factories this entry point supports, keyed by {@link ClientConfig} transport property.
     * The browser entry has `webSocket` only; the Node entry's `NexusClient` adds the rest.
     *
     * @type {Readonly<Partial<Record<import('../contract/config.js').TransportKind, import('../transport/ITransport.js').TransportFactory>>>}
     */
    static transports: Readonly<Partial<Record<import('../contract/config.js').TransportKind, import('../transport/ITransport.js').TransportFactory>>>;
    /**
     * Creates a client of the class it is called on. Validates `config` and throws `TypeError` when it is invalid
     * (no transport, several transports, or a transport this entry point does not support).
     *
     * @template {import('../contract/nexus.js').ClientNexusBase<TP>} TN
     * @template {import('../contract/proxy.js').ProxyBase} TP
     * @param {import('../contract/config.js').ClientConfig} config
     * @param {TN} nexus
     * @param {import('../contract/handshake.js').ClientBinding<TP>} binding Supplied by the generated factory.
     * @returns {NexusClient<TN, TP>}
     */
    static create<TN extends import('../contract/nexus.js').ClientNexusBase<TP>, TP extends import('../contract/proxy.js').ProxyBase>(config: import('../contract/config.js').ClientConfig, nexus: TN, binding: import('../contract/handshake.js').ClientBinding<TP>): NexusClient<TN, TP>;
    /**
     * Use {@link NexusClient.create}.
     *
     * @param {import('../contract/config.js').ClientConfig} config
     * @param {TNexus} nexus
     * @param {import('../contract/handshake.js').ClientBinding<TProxy>} binding
     */
    constructor(config: import('../contract/config.js').ClientConfig, nexus: TNexus, binding: import('../contract/handshake.js').ClientBinding<TProxy>);
    /** @returns {import('../contract/enums.js').ConnectionStateValue} */
    get state(): import('../contract/enums.js').ConnectionStateValue;
    /** The server proxy. Throws before the first successful connect. @returns {TProxy} */
    get proxy(): TProxy;
    /** True when the transport is TLS, WSS, HTTPS or UDS. Plaintext is allowed but logs a warning once per client. @returns {boolean} */
    get isSecure(): boolean;
    /**
     * The server certificate of the current connection: Node's `X509Certificate` (`node:crypto`) on the Node TLS
     * transports (`tcpTls`, `wss:` WebSocket, `https:` HttpSocket). Null when not connected, and on every other
     * transport, the browser WebSocket included (browsers do not expose it).
     * @returns {unknown}
     */
    get remoteCertificate(): unknown;
    /**
     * The server's address on the current connection (C# `Context.RemoteAddress`, Java `remoteAddress()`): the IP
     * address of the Node socket's peer on every Node transport, or the socket path for UDS. Null when not connected, and
     * always in the browser, which does not expose it.
     * @returns {string | null}
     */
    get remoteAddress(): string | null;
    /**
     * The server's port on the current connection (C# `Context.RemotePort`, Java `remotePort()`) on the Node TCP-based
     * transports. Null when not connected, for UDS, and always in the browser.
     * @returns {number | null}
     */
    get remotePort(): number | null;
    /** Resolves when the current session ends (after reconnection gives up). @returns {Promise<void>} */
    get disconnected(): Promise<void>;
    /**
     * The current session, or null. Runtime-internal (invocations, pipes, reconnection).
     * @returns {NexusSession | null}
     */
    get _session(): NexusSession | null;
    /**
     * Connects and completes the handshake. Rejects with `NexusTransportError` on failure and the abort reason
     * when `signal` aborts. A connect the server refuses for its authentication (`DisconnectReason.Authentication`
     * during the handshake), or an `authenticate` that exceeds `handshakeTimeout`, rejects with `transportError`
     * `AuthenticationError`, as C# `ConnectAsync` throws `TransportException(AuthenticationError)`.
     *
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<void>}
     */
    connect(options?: {
        signal?: AbortSignal;
    }): Promise<void>;
    /**
     * Like {@link NexusClient#connect}, but resolves a result instead of rejecting.
     *
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<ConnectionResult>}
     */
    tryConnect(options?: {
        signal?: AbortSignal;
    }): Promise<ConnectionResult>;
    /**
     * Typed for the events of {@link NexusClientEventMap}; any other type takes a plain listener.
     * @template {keyof NexusClientEventMap} K
     * @overload
     * @param {K} type
     * @param {(this: NexusClient<TNexus, TProxy>, event: NexusClientEventMap[K]) => void} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     */
    addEventListener<K extends keyof NexusClientEventMap>(type: K, listener: (this: NexusClient<TNexus, TProxy>, event: NexusClientEventMap[K]) => void, options?: boolean | AddEventListenerOptions): void;
    /**
     * Typed for the events of {@link NexusClientEventMap}; any other type takes a plain listener.
     * @template {keyof NexusClientEventMap} K
     * @overload
     * @param {K} type
     * @param {(this: NexusClient<TNexus, TProxy>, event: NexusClientEventMap[K]) => void} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     */
    addEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | AddEventListenerOptions): void;
    /**
     * Typed like {@link NexusClient#addEventListener}.
     * @template {keyof NexusClientEventMap} K
     * @overload
     * @param {K} type
     * @param {(this: NexusClient<TNexus, TProxy>, event: NexusClientEventMap[K]) => void} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     */
    removeEventListener<K extends keyof NexusClientEventMap>(type: K, listener: (this: NexusClient<TNexus, TProxy>, event: NexusClientEventMap[K]) => void, options?: boolean | EventListenerOptions): void;
    /**
     * Typed like {@link NexusClient#addEventListener}.
     * @template {keyof NexusClientEventMap} K
     * @overload
     * @param {K} type
     * @param {(this: NexusClient<TNexus, TProxy>, event: NexusClientEventMap[K]) => void} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     */
    removeEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | EventListenerOptions): void;
    /** Disconnects gracefully and stops reconnection. @returns {Promise<void>} */
    disconnect(): Promise<void>;
    /** The reconnection controller, or null before the first connect and when reconnection is off. @returns {ReconnectController | null} */
    get reconnection(): ReconnectController | null;
    /**
     * Creates a pipe to pass to a pipe method. It reserves a pipe slot and receive window until it is disposed.
     * Throws `NexusTransportError` when not connected and `NexusBusyError` when `maxPipes` or the session budget is
     * reached.
     * @returns {import('../contract/pipes.js').NexusDuplexPipe}
     */
    createPipe(): import('../contract/pipes.js').NexusDuplexPipe;
    /**
     * Creates a channel to pass to a channel method. A generated class formatter (and `stringFormatter`) is typed as
     * reading `T | null`; this overload gives a `NexusDuplexChannel<T>`, the type a channel parameter of a generated
     * proxy takes. Pass the type argument (`createChannel<T | null>(formatter)`) for a channel of nullable items.
     * @template T
     * @overload
     * @param {import('../contract/codec.js').Formatter<T | null>} formatter
     * @returns {import('../contract/pipes.js').NexusDuplexChannel<T>}
     */
    /**
     * Creates a channel to pass to a channel method.
     * @template T
     * @overload
     * @param {import('../contract/codec.js').Formatter<T>} formatter
     * @returns {import('../contract/pipes.js').NexusDuplexChannel<T>}
     */
    /**
     * @param {import('../contract/codec.js').Formatter<any>} formatter
     * @returns {import('../contract/pipes.js').NexusDuplexChannel<any>}
     */
    createChannel<T>(formatter: import('../contract/codec.js').Formatter<T | null>): import('../contract/pipes.js').NexusDuplexChannel<T>;
    /**
     * Creates a channel to pass to a channel method. A generated class formatter (and `stringFormatter`) is typed as
     * reading `T | null`; this overload gives a `NexusDuplexChannel<T>`, the type a channel parameter of a generated
     * proxy takes. Pass the type argument (`createChannel<T | null>(formatter)`) for a channel of nullable items.
     * @template T
     * @overload
     * @param {import('../contract/codec.js').Formatter<T | null>} formatter
     * @returns {import('../contract/pipes.js').NexusDuplexChannel<T>}
     */
    /**
     * Creates a channel to pass to a channel method.
     * @template T
     * @overload
     * @param {import('../contract/codec.js').Formatter<T>} formatter
     * @returns {import('../contract/pipes.js').NexusDuplexChannel<T>}
     */
    /**
     * @param {import('../contract/codec.js').Formatter<any>} formatter
     * @returns {import('../contract/pipes.js').NexusDuplexChannel<any>}
     */
    createChannel<T>(formatter: import('../contract/codec.js').Formatter<T>): import('../contract/pipes.js').NexusDuplexChannel<T>;
    /**
     * Diagnostic snapshot, field names as C# `NexusClientSnapshot` camelCased; null when not connected.
     * The session layer fills the session fields; reconnection, pool and metrics fill the rest.
     * @returns {object | null}
     */
    getSnapshot(): object | null;
    /** Same as {@link NexusClient#disconnect}. @returns {Promise<void>} */
    [Symbol.asyncDispose](): Promise<void>;
    /**
     * One connect attempt (C# `TryConnectAsyncCore`). The reconnect loop calls it with `isReconnecting`.
     *
     * @protected
     * @param {boolean} isReconnecting
     * @param {AbortSignal} [signal]
     * @returns {Promise<ConnectionResult>}
     */
    protected _tryConnectCore(isReconnecting: boolean, signal?: AbortSignal): Promise<ConnectionResult>;
}
