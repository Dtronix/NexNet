/**
 * A client over a LoopbackTransport with a ManualClock, a scriptable client nexus and helpers that play the server:
 * frames to send and frames the client wrote.
 * @module
 */

import { ClientNexusBase } from '../../../src/client/ClientNexusBase.js';
import { ProxyBase } from '../../../src/client/ProxyBase.js';
import { NexusClient } from '../../../src/client/NexusClient.js';
import { FrameReader } from '../../../src/session/FrameReader.js';
import { encodeChunkFrame, encodeFrame } from '../../../src/session/frames.js';
import { MessageType } from '../../../src/session/protocol.js';
import { encodeInvocationCancellation, encodeInvocationCredit, encodeMessageAbort } from '../../../src/session/wire.js';
import { encodeInvocation, encodeInvocationResult, encodeMessageStart } from '../../../src/invocation/messages.js';
import { LoopbackTransport, concat } from '../../session/support/LoopbackTransport.js';
import { ManualClock } from '../../session/support/ManualClock.js';
import { CLIENT_HASH, SERVER_HASH, serverHello, tick } from '../../session/support/harness.js';

export { tick };

/** A proxy exposing the protected invoke members. */
export class TestProxy extends ProxyBase {
  /**
   * @template [T=void]
   * @param {number} methodId
   * @param {import('../../../src/client/ProxyBase.js').ArgumentsWriter | null} args
   * @param {import('../../../src/client/ProxyBase.js').InvokeOptions<T>} [options]
   * @returns {Promise<T>}
   */
  invoke(methodId, args, options) {
    return this._invoke(methodId, args, options);
  }

  /**
   * @param {number} methodId
   * @param {import('../../../src/client/ProxyBase.js').ArgumentsWriter | null} args
   */
  invokeVoid(methodId, args) {
    this._invokeVoid(methodId, args);
  }
}

/**
 * @typedef {(reader: import('../../../src/contract/codec.js').MsgPackReader, ctx: import('../../../src/client/ClientNexusBase.js').DispatchContext) => unknown} MethodImpl
 */

/**
 * A client nexus whose methods the test sets per method ID.
 * @extends {ClientNexusBase<TestProxy>}
 */
export class ScriptedNexus extends ClientNexusBase {
  /** @type {Map<number, MethodImpl>} */
  methods = new Map();
  /** @type {Set<number>} */
  separate = new Set();
  /** @type {Array<{ methodId: number, invocationId: number, signal: AbortSignal }>} */
  dispatched = [];
  /** @type {((error: unknown, methodId: number) => any) | null} */
  exceptionHook = null;

  /**
   * @param {number} methodId
   * @returns {boolean}
   */
  _separatePool(methodId) {
    return this.separate.has(methodId);
  }

  /**
   * @param {unknown} error
   * @param {number} methodId
   */
  onInvocationException(error, methodId) {
    return this.exceptionHook ? this.exceptionHook(error, methodId) : null;
  }

  /**
   * @param {number} methodId
   * @param {import('../../../src/contract/codec.js').MsgPackReader} reader
   * @param {import('../../../src/client/ClientNexusBase.js').DispatchContext} ctx
   */
  _dispatch(methodId, reader, ctx) {
    this.dispatched.push({ methodId, invocationId: ctx.invocationId, signal: ctx.signal });
    const impl = this.methods.get(methodId);
    if (!impl) return super._dispatch(methodId, reader, ctx);
    return /** @type {void | Promise<void>} */ (impl(reader, ctx));
  }
}

/**
 * @typedef {object} Frame
 * @property {number} type
 * @property {Uint8Array} body
 * @property {number} invocationId
 * @property {number} kind
 */

/**
 * Parses everything the client wrote (preamble included) into frames, greetings excluded.
 * @param {Uint8Array} bytes
 * @returns {Frame[]}
 */
export function parseFrames(bytes) {
  const reader = new FrameReader();
  reader.handshakeComplete = true;
  /** @type {Frame[]} */
  const frames = [];
  const result = reader.push(bytes, (f) => {
    if (f.type !== MessageType.ClientGreeting && f.type !== MessageType.ClientGreetingReconnection) {
      frames.push({ type: f.type, body: f.body.slice(), invocationId: f.invocationId, kind: f.kind });
    }
    return true;
  });
  if (result.kind !== 'more') frames.push({ type: result.kind === 'disconnect' ? result.reason : -1, body: new Uint8Array(0), invocationId: 0, kind: 0 });
  return frames;
}

/**
 * @typedef {object} HarnessOptions
 * @property {Partial<import('../../../src/contract/config.js').ClientConfig>} [config]
 * @property {Set<number> | BigUint64Array} [acceptedIds]
 * @property {Partial<import('../../../src/session/wire.js').ServerGreeting>} [greeting]
 * @property {Uint8Array} [afterHello] Bytes sent in the same transport read as the ServerGreeting.
 * @property {(nexus: ScriptedNexus) => void} [setup] Runs before the connect.
 */

/**
 * Builds and connects a client.
 * @param {HarnessOptions} [options]
 */
export async function connectedHarness({ config = {}, acceptedIds = new Set([0, 1, 2, 3]), greeting = {}, afterHello, setup } = {}) {
  const clock = new ManualClock();
  /** @type {Array<[string, string, unknown]>} */
  const logs = [];
  /** @type {LoopbackTransport[]} */
  const transports = [];
  /** @type {import('../../../src/transport/ITransport.js').TransportFactory} */
  const factory = async () => {
    const t = new LoopbackTransport();
    transports.push(t);
    return t;
  };
  /** @extends {NexusClient<any, any>} */
  class HarnessClient extends NexusClient {
    static transports = Object.freeze({ tcp: factory, webSocket: factory });
  }
  const nexus = new ScriptedNexus();
  const client = HarnessClient.create(
    /** @type {import('../../../src/contract/config.js').ClientConfig} */ ({
      tcp: { host: 'localhost', port: 1 },
      clock,
      logger: { log: (level, message, error) => logs.push([level, message, error]) },
      disconnectDelay: 0,
      ...config,
    }),
    nexus,
    { handshake: { version: null, serverNexusHash: SERVER_HASH, clientNexusHash: CLIENT_HASH, acceptedIds }, proxy: TestProxy },
  );

  const h = {
    client,
    nexus,
    clock,
    logs,
    transports,
    /** @returns {LoopbackTransport} */
    get transport() {
      return transports[transports.length - 1];
    },
    /** @returns {TestProxy} */
    get proxy() {
      return /** @type {TestProxy} */ (client.proxy);
    },
    /** @returns {import('../../../src/session/NexusSession.js').NexusSession} */
    get session() {
      const s = client._session;
      if (!s) throw new Error('no session');
      return s;
    },
    /** Frames the client wrote since the handshake (pings excluded). @returns {Frame[]} */
    frames() {
      return parseFrames(this.transport.sent).filter((f) => f.type !== MessageType.Ping);
    },
    /** Frames of one type. @param {number} type @returns {Frame[]} */
    framesOf(type) {
      return this.frames().filter((f) => f.type === type);
    },
    /** @param {Uint8Array} bytes */
    send(bytes) {
      this.transport.serverSend(bytes);
    },
    /** @param {number} invocationId @param {number} state @param {Uint8Array | null} [value] */
    sendResult(invocationId, state, value = null) {
      this.send(encodeFrame(MessageType.InvocationResult, encodeInvocationResult(invocationId, state, value)));
    },
    /** @param {number} invocationId @param {number} methodId @param {number} flags @param {Uint8Array | null} args */
    sendInvocation(invocationId, methodId, flags, args) {
      this.send(encodeFrame(MessageType.Invocation, encodeInvocation(invocationId, methodId, flags, args)));
    },
    /** @param {number} credits */
    sendCredit(credits) {
      this.send(encodeFrame(MessageType.InvocationCredit, encodeInvocationCredit(credits)));
    },
    /** @param {number} invocationId */
    sendCancellation(invocationId) {
      this.send(encodeFrame(MessageType.InvocationCancellation, encodeInvocationCancellation(invocationId)));
    },
    /** @param {number} invocationId @param {number} kind @param {number} reason */
    sendAbort(invocationId, kind, reason) {
      this.send(encodeFrame(MessageType.MessageAbort, encodeMessageAbort(invocationId, kind, reason)));
    },
    /** @param {import('../../../src/invocation/messages.js').MessageStart} start */
    sendStart(start) {
      this.send(encodeFrame(MessageType.MessageStart, encodeMessageStart(start)));
    },
    /** @param {number} invocationId @param {number} kind @param {Uint8Array} payload */
    sendChunk(invocationId, kind, payload) {
      this.send(encodeChunkFrame(invocationId, kind, payload));
    },
  };

  setup?.(nexus);
  const result = client.tryConnect();
  await tick();
  h.transport.serverSend(afterHello ? concat([serverHello(greeting), afterHello]) : serverHello(greeting));
  const r = await result;
  if (!r.success) throw new Error(`connect failed: ${JSON.stringify(r)}`);
  await tick();
  return h;
}

/**
 * Waits until `predicate` holds, running pending tasks in between (no clock involved).
 * @param {() => boolean} predicate
 * @param {number} [rounds]
 */
export async function until(predicate, rounds = 50) {
  for (let i = 0; i < rounds; i++) {
    if (predicate()) return;
    await tick();
  }
  throw new Error('condition not reached');
}
