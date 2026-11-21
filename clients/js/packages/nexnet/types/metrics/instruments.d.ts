/**
 * The NexNet instruments: names, kinds, units and tags identical to C# `NexusMetrics` (docs/articles/metrics.md).
 * @module
 */
/** Tag keys. */
export declare const MetricTags: Readonly<{
    role: "nexnet.role";
    transport: "nexnet.transport";
    endpoint: "nexnet.endpoint";
    direction: "nexnet.direction";
    disconnectReason: "nexnet.disconnect.reason";
    phase: "nexnet.session.phase";
    rejectionReason: "nexnet.rejection.reason";
    resetReason: "nexnet.reset.reason";
    refusalSide: "nexnet.refusal.side";
    abortReason: "nexnet.abort.reason";
    invocationPool: "nexnet.invocation.pool";
}>;
/** Name of the meter (C# `NexusMeter.Name`). */
export declare const METER_NAME = "NexNet";
export type InstrumentKind = 'observableCounter' | 'observableUpDownCounter' | 'counter' | 'histogram';
export type InstrumentDescriptor = {
    name: string;
    kind: InstrumentKind;
    unit: string;
    description: string;
    /**
     * Tag keys beyond `nexnet.role` and `nexnet.transport` (and `nexnet.endpoint` on servers).
     */
    tags: readonly string[];
    /**
     * True when only a server records it.
     */
    serverOnly: boolean;
    /**
     * Histogram bucket boundaries advised by C#, in the instrument's unit.
     */
    buckets?: readonly number[];
};
/** Every instrument of the C# meter, by camelCase key. */
export declare const Instruments: Readonly<{
    sessionActive: Readonly<InstrumentDescriptor>;
    sessionHandshaking: Readonly<InstrumentDescriptor>;
    sessionDisconnects: Readonly<InstrumentDescriptor>;
    sessionDuration: Readonly<InstrumentDescriptor>;
    connectionRejections: Readonly<InstrumentDescriptor>;
    ioFrames: Readonly<InstrumentDescriptor>;
    ioBytes: Readonly<InstrumentDescriptor>;
    budgetReserved: Readonly<InstrumentDescriptor>;
    budgetLimit: Readonly<InstrumentDescriptor>;
    pipeOpen: Readonly<InstrumentDescriptor>;
    pipeWindow: Readonly<InstrumentDescriptor>;
    pipeResets: Readonly<InstrumentDescriptor>;
    pipeRefusals: Readonly<InstrumentDescriptor>;
    invocationQueued: Readonly<InstrumentDescriptor>;
    invocationExecuting: Readonly<InstrumentDescriptor>;
    invocationCreditWaits: Readonly<InstrumentDescriptor>;
    invocationBusy: Readonly<InstrumentDescriptor>;
    invocationCreditDrops: Readonly<InstrumentDescriptor>;
    invocationQueueWait: Readonly<InstrumentDescriptor>;
    pipeCreditWait: Readonly<InstrumentDescriptor>;
    largeMessageInFlight: Readonly<InstrumentDescriptor>;
    largeMessageAborts: Readonly<InstrumentDescriptor>;
}>;
export type InstrumentKey = keyof typeof Instruments;
/** @typedef {keyof typeof Instruments} InstrumentKey */
/**
 * C# `NexusMetrics.ToSnakeCase`: `SocketError` → `socket_error`.
 * @param {string} name
 * @returns {string}
 */
export declare function toSnakeCase(name: string): string;
/**
 * The `nexnet.disconnect.reason` tag value of a disconnect code: the `DisconnectReason` name in snake case, or
 * `unknown` for an unnamed code.
 * @param {number} reason
 * @returns {string}
 */
export declare function disconnectReasonTag(reason: number): string;
/** `nexnet.transport` tag value per {@link import('../contract/config.js').TransportKind} (C# `TransportName`). */
export declare const TransportTags: Readonly<{
    webSocket: "websocket";
    httpSocket: "httpsocket";
    tcp: "tcp";
    tcpTls: "tls";
    uds: "uds";
}>;
/**
 * The `nexnet.transport` tag value of a client configuration; `custom` when no known transport is set.
 * @param {import('../contract/config.js').ClientConfig} config
 * @returns {string}
 */
export declare function transportTagOf(config: import('../contract/config.js').ClientConfig): string;
