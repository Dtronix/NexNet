/**
 * `@dtronix/nexnet`: platform-neutral entry (the `default` export condition).
 * Re-exports the runtime API that generated code and applications use. Must not import `node:*`.
 * @module
 */

export { MsgPackWriter, MsgPackReader, FormatterRegistry, tupleFormatter } from './contract/codec.js';
// The built-in formatters generated code composes, the decode error and the serializer presets.
export {
  NexusSerializationError,
  NexusSerializerOptions,
  boolFormatter,
  sbyteFormatter,
  byteFormatter,
  int16Formatter,
  uint16Formatter,
  int32Formatter,
  uint32Formatter,
  int64Formatter,
  uint64Formatter,
  int64NumberFormatter,
  uint64NumberFormatter,
  float32Formatter,
  float64Formatter,
  halfFormatter,
  charFormatter,
  stringFormatter,
  binFormatter,
  decimalFormatter,
  dateTimeFormatter,
  dateTimeOffsetFormatter,
  timeSpanFormatter,
  dateOnlyFormatter,
  timeOnlyFormatter,
  guidFormatter,
  bigIntegerFormatter,
  uriFormatter,
  versionFormatter,
  rawFormatter,
  int8ArrayFormatter,
  int16ArrayFormatter,
  uint16ArrayFormatter,
  int32ArrayFormatter,
  uint32ArrayFormatter,
  int64ArrayFormatter,
  uint64ArrayFormatter,
  float32ArrayFormatter,
  float64ArrayFormatter,
  charArrayFormatter,
  halfArrayFormatter,
  nullableFormatter,
  arrayFormatter,
  setFormatter,
  mapFormatter,
  keyValuePairFormatter,
} from './serialization/index.js';
export { parseGuid, guidToBytes, guidFromBytes, guidToDotNetBytes, guidFromDotNetBytes, EMPTY_GUID } from './values/index.js';
export {
  DateTimeKind,
  NexusDecimal,
  NexusDateTime,
  NexusDateTimeOffset,
  NexusTimeSpan,
  NexusDateOnly,
  NexusTimeOnly,
  NexusVersion,
  NexusVersion as Version,
  tuple,
} from './contract/values.js';
export {
  ConnectionState,
  ConnectionResultState,
  DisconnectReason,
  RemoteExceptionDetail,
  NexusCollectionState,
} from './contract/enums.js';
export {
  NexusError,
  NexusTransportError,
  NexusRemoteInvocationError,
  NexusUnauthorizedError,
  NexusBusyError,
  NexusMessageTooLargeError,
  cancellationError,
  isCancellationError,
} from './contract/errors.js';
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
export {
  DefaultReconnectionPolicy,
  DEFAULT_RECONNECT_DELAYS,
  DEFAULT_RECONNECT_REASONS,
  ReconnectController,
} from './reconnect/index.js';
export { NexusMetrics, Instruments, MetricTags, METER_NAME } from './metrics/index.js';
export { diagnostics } from './diagnostics/index.js';

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
