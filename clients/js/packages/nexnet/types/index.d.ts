/**
 * `@dtronix/nexnet`: platform-neutral entry (the `default` export condition).
 * Re-exports the runtime API that generated code and applications use. Must not import `node:*`.
 * @module
 */
export { MsgPackWriter, MsgPackReader, FormatterRegistry, tupleFormatter } from './contract/codec.js';
export { NexusSerializationError, NexusSerializerOptions, boolFormatter, sbyteFormatter, byteFormatter, int16Formatter, uint16Formatter, int32Formatter, uint32Formatter, int64Formatter, uint64Formatter, int64NumberFormatter, uint64NumberFormatter, float32Formatter, float64Formatter, halfFormatter, charFormatter, stringFormatter, binFormatter, decimalFormatter, dateTimeFormatter, dateTimeOffsetFormatter, timeSpanFormatter, dateOnlyFormatter, timeOnlyFormatter, guidFormatter, bigIntegerFormatter, uriFormatter, versionFormatter, rawFormatter, int8ArrayFormatter, int16ArrayFormatter, uint16ArrayFormatter, int32ArrayFormatter, uint32ArrayFormatter, int64ArrayFormatter, uint64ArrayFormatter, float32ArrayFormatter, float64ArrayFormatter, charArrayFormatter, halfArrayFormatter, nullableFormatter, arrayFormatter, setFormatter, mapFormatter, keyValuePairFormatter, } from './serialization/index.js';
export { parseGuid, guidToBytes, guidFromBytes, guidToDotNetBytes, guidFromDotNetBytes, EMPTY_GUID } from './values/index.js';
export { DateTimeKind, NexusDecimal, NexusDateTime, NexusDateTimeOffset, NexusTimeSpan, NexusDateOnly, NexusTimeOnly, NexusVersion, NexusVersion as Version, tuple, } from './contract/values.js';
export { ConnectionState, ConnectionResultState, DisconnectReason, RemoteExceptionDetail, NexusCollectionState, } from './contract/enums.js';
export { NexusError, NexusTransportError, NexusRemoteInvocationError, NexusUnauthorizedError, NexusBusyError, NexusMessageTooLargeError, cancellationError, isCancellationError, } from './contract/errors.js';
export { TransportKinds } from './contract/config.js';
export { NexusClient } from './contract/client.js';
export { ProxyBase } from './contract/proxy.js';
export { ClientNexusBase } from './contract/nexus.js';
export { NexusDuplexPipe, NexusChannelReader, NexusChannelWriter, NexusDuplexChannel } from './contract/pipes.js';
export { NexusList } from './contract/collections.js';
export { NexusCollectionMode } from './collections/mode.js';
export { NexusClientPool } from './contract/pool.js';
export { NexusClientPoolConnectionError } from './pool/NexusClientPool.js';
export { CoreLogger, ConsoleLogger, RollingLogger, NexusLogBehaviors, NexusLogLevelOrder } from './logging/index.js';
export { DefaultReconnectionPolicy, DEFAULT_RECONNECT_DELAYS, DEFAULT_RECONNECT_REASONS, ReconnectController, } from './reconnect/index.js';
export { NexusMetrics, Instruments, MetricTags, METER_NAME } from './metrics/index.js';
export { diagnostics } from './diagnostics/index.js';
export type Formatter<T> = import('./contract/codec.js').Formatter<T>;
export type SerializerOptions = import('./contract/codec.js').SerializerOptions;
export type ClientConfig = import('./contract/config.js').ClientConfig;
export type TlsOptions = import('./contract/config.js').TlsOptions;
export type WebSocketTransportConfig = import('./contract/config.js').WebSocketTransportConfig;
export type HttpSocketTransportConfig = import('./contract/config.js').HttpSocketTransportConfig;
export type TcpTransportConfig = import('./contract/config.js').TcpTransportConfig;
export type TcpTlsTransportConfig = import('./contract/config.js').TcpTlsTransportConfig;
export type UdsTransportConfig = import('./contract/config.js').UdsTransportConfig;
export type TransportKind = import('./contract/config.js').TransportKind;
export type ReconnectionPolicy = import('./contract/config.js').ReconnectionPolicy;
export type NexusLogger = import('./contract/config.js').NexusLogger;
export type NexusLogLevel = import('./contract/config.js').NexusLogLevel;
export type AuthenticateCallback = import('./contract/config.js').AuthenticateCallback;
export type Transport = import('./contract/transport.js').Transport;
export type TransportFactory = import('./contract/transport.js').TransportFactory;
export type HandshakeDescriptor = import('./contract/handshake.js').HandshakeDescriptor;
export type ClientBinding<TProxy extends import('./contract/proxy.js').ProxyBase> = import('./contract/handshake.js').ClientBinding<TProxy>;
export type ConnectionResult = import('./contract/client.js').ConnectionResult;
export type ArgumentsWriter = import('./contract/proxy.js').ArgumentsWriter;
export type PipeArgumentsWriter = import('./contract/proxy.js').PipeArgumentsWriter;
export type ClientContext<TProxy extends import('./contract/proxy.js').ProxyBase> = import('./contract/nexus.js').ClientContext<TProxy>;
export type DispatchContext = import('./contract/nexus.js').DispatchContext;
export type NexusClientPoolConfig = import('./contract/pool.js').NexusClientPoolConfig;
/**
 * @template T
 * @typedef {import('./contract/codec.js').Formatter<T>} Formatter
 */
/** @typedef {import('./contract/codec.js').SerializerOptions} SerializerOptions */
/** @typedef {import('./contract/config.js').ClientConfig} ClientConfig */
/** @typedef {import('./contract/config.js').TlsOptions} TlsOptions */
/** @typedef {import('./contract/config.js').WebSocketTransportConfig} WebSocketTransportConfig */
/** @typedef {import('./contract/config.js').HttpSocketTransportConfig} HttpSocketTransportConfig */
/** @typedef {import('./contract/config.js').TcpTransportConfig} TcpTransportConfig */
/** @typedef {import('./contract/config.js').TcpTlsTransportConfig} TcpTlsTransportConfig */
/** @typedef {import('./contract/config.js').UdsTransportConfig} UdsTransportConfig */
/** @typedef {import('./contract/config.js').TransportKind} TransportKind */
/** @typedef {import('./contract/config.js').ReconnectionPolicy} ReconnectionPolicy */
/** @typedef {import('./contract/config.js').NexusLogger} NexusLogger */
/** @typedef {import('./contract/config.js').NexusLogLevel} NexusLogLevel */
/** @typedef {import('./contract/config.js').AuthenticateCallback} AuthenticateCallback */
/** @typedef {import('./contract/transport.js').Transport} Transport */
/** @typedef {import('./contract/transport.js').TransportFactory} TransportFactory */
/** @typedef {import('./contract/handshake.js').HandshakeDescriptor} HandshakeDescriptor */
/**
 * @template {import('./contract/proxy.js').ProxyBase} TProxy
 * @typedef {import('./contract/handshake.js').ClientBinding<TProxy>} ClientBinding
 */
/** @typedef {import('./contract/client.js').ConnectionResult} ConnectionResult */
/** @typedef {import('./contract/proxy.js').ArgumentsWriter} ArgumentsWriter */
/** @typedef {import('./contract/proxy.js').PipeArgumentsWriter} PipeArgumentsWriter */
/**
 * @template {import('./contract/proxy.js').ProxyBase} TProxy
 * @typedef {import('./contract/nexus.js').ClientContext<TProxy>} ClientContext
 */
/** @typedef {import('./contract/nexus.js').DispatchContext} DispatchContext */
/** @typedef {import('./contract/pool.js').NexusClientPoolConfig} NexusClientPoolConfig */
