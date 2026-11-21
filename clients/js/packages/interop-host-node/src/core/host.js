/**
 * The language host (docs/internals/interop-control-protocol.md): the dispatcher of the control protocol over the
 * JavaScript client runtime, with test scopes, handles, scripts, pipes, channels, collections and pools. Platform-neutral:
 * the Node host (`src/node`) and the browser host (`interop-host-browser`) give it a {@link Platform}.
 * @module
 */

import {
  ConnectionState,
  NexusClient,
  NexusClientPool,
  NexusClientPoolConnectionError,
  NexusDuplexChannel,
  NexusError,
  cancellationError,
  isCancellationError,
} from '@dtronix/nexnet';
import { encodeFrame, ControlProtocolError } from './framing.js';
import { HostError, ThrowingWatchError, badRequest, notSupported, unknownHandle, invalidOperation, toWireError, nativeError, messageOf, NOT_CONNECTED } from './errors.js';
import { stateName, stateValue, reasonName, resultStateName, levelOrder, levelName, runtimeLevel, collectionModeName, collectionStateName } from './names.js';
import { parseSpec, parsePoolOptions } from './spec.js';
import { FakeClock, TrackingClock, systemClock } from './clock.js';
import { ClientScripts, SILENT_SCRIPTS, validateSteps, isChannelClosed } from './scripts.js';
import { toBase64, fromBase64, pattern, Sha256 } from './encoding.js';

/** Control protocol version (§4.1). */
export const PROTOCOL_VERSION = 1;

/** Ops that take no test scope (§6.2). */
const HOST_OPS = new Set(['ping', 'host.dumpThreads', 'host.shutdown', 'log.level']);

/**
 * Capabilities of the JavaScript client runtime itself, whatever the platform: added to the platform's in `hello`.
 */
const RUNTIME_CAPABILITIES = ['PipeInputCompletion', 'ChannelReadCancellation'];

/** Bytes per pattern chunk written to a pipe. */
const PATTERN_CHUNK = 16 * 1024;

/** How long the leak audit waits for things to settle (§5.1). */
const AUDIT_SETTLE_MS = 1000;

/**
 * What the host needs from its platform.
 * @typedef {object} Platform
 * @property {string} lang The language name (`node`, `browser-chromium`, …).
 * @property {string} token The `--token` argument.
 * @property {string} runtime Runtime name and version.
 * @property {string} lib Client library and version.
 * @property {number} [pid] The process id (absent in a browser).
 * @property {readonly string[]} capabilities `ClientCapabilities` names.
 * @property {readonly string[]} transports `ClientTransport` names.
 * @property {number} maxConcurrency
 * @property {boolean} browser
 * @property {(frame: Uint8Array) => void} send Writes one whole frame to the control connection.
 * @property {() => void} close Closes the control connection once the frames written are sent.
 * @property {(code: number) => void} exit Ends the host.
 * @property {() => string} dumpThreads
 */

/**
 * @typedef {object} LeakReport
 * @property {number} openClients
 * @property {number} openPipes
 * @property {number} timers
 * @property {number} pendingInvocations
 * @property {number} subscriptions
 * @property {string[]} details
 */

/**
 * @typedef {object} Context
 * @property {AbortSignal} signal Aborted when the request ends (response sent, timeout or scope end).
 * @property {Scope | null} scope
 * @property {string | null} test
 */

/** @returns {LeakReport} */
function emptyReport() {
  return { openClients: 0, openPipes: 0, timers: 0, pendingInvocations: 0, subscriptions: 0, details: [] };
}

/** @param {LeakReport} report @returns {boolean} */
function isClean(report) {
  return report.openClients + report.openPipes + report.timers + report.pendingInvocations + report.subscriptions === 0;
}

/** @param {number} ms @returns {Promise<void>} */
function sleep(ms) {
  return new Promise((resolve) => globalThis.setTimeout(resolve, ms));
}

/**
 * Rejects with the signal's reason when it aborts first.
 * @template T
 * @param {Promise<T>} promise
 * @param {AbortSignal} signal
 * @returns {Promise<T>}
 */
function abortable(promise, signal) {
  if (signal.aborted) return Promise.reject(signal.reason);
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(signal.reason);
    signal.addEventListener('abort', onAbort, { once: true });
    promise.then(
      (value) => {
        signal.removeEventListener('abort', onAbort);
        resolve(value);
      },
      (error) => {
        signal.removeEventListener('abort', onAbort);
        reject(error);
      },
    );
  });
}

/**
 * @param {unknown} value
 * @param {string} name
 * @returns {string}
 */
function requireString(value, name) {
  if (typeof value !== 'string' || value.length === 0) throw badRequest('Missing string argument \'' + name + '\'.');
  return value;
}

/**
 * @param {unknown} value
 * @param {string} name
 * @param {number} [min]
 * @returns {number}
 */
function requireInt(value, name, min) {
  if (typeof value !== 'number' || !Number.isInteger(value)) throw badRequest('Missing integer argument \'' + name + '\'.');
  if (min !== undefined && value < min) throw badRequest('Argument \'' + name + '\' is below ' + min + ': ' + value + '.');
  return value;
}

/** A test scope (§5). */
class Scope {
  /** @param {string} id */
  constructor(id) {
    this.id = id;
    this.controller = new AbortController();
    /** @type {Set<string>} */
    this.handles = new Set();
    /** @type {Map<string, AbortController>} */
    this.tokens = new Map();
  }
}

/** Common state of a client and a rental: what `invoke` needs. */
class InvokerEntry {
  /**
   * @param {string} kind
   * @param {string} h
   * @param {Scope} scope
   * @param {import('./types.js').ScaffoldingExport} exp
   */
  constructor(kind, h, scope, exp) {
    this.kind = kind;
    this.h = h;
    this.scope = scope;
    this.exp = exp;
    /** Invocations started and not completed. */
    this.pending = 0;
    /** Host timers (afterMs cancellations, script delays). @type {Set<unknown>} */
    this.timers = new Set();
    this.disposed = false;
  }

  /** @returns {any} */
  get proxy() {
    throw new Error('abstract');
  }

  /**
   * A host timer, counted while it runs.
   * @param {() => void} callback
   * @param {number} ms
   * @returns {() => void} Cancels it.
   */
  timer(callback, ms) {
    const handle = globalThis.setTimeout(() => {
      this.timers.delete(handle);
      callback();
    }, ms);
    this.timers.add(handle);
    return () => {
      if (this.timers.delete(handle)) globalThis.clearTimeout(handle);
    };
  }
}

/** A client of `client.create`. */
class ClientEntry extends InvokerEntry {
  /**
   * @param {string} h
   * @param {Scope} scope
   * @param {import('./types.js').ScaffoldingExport} exp
   * @param {TrackingClock} clock
   * @param {FakeClock | null} fake
   */
  constructor(h, scope, exp, clock, fake) {
    super('client', h, scope, exp);
    this.clock = clock;
    this.fake = fake;
    this.lifetime = new AbortController();
    /** @type {any} */
    this.client = null;
    /** @type {Record<string, any>} */
    this.config = {};
    /** @type {ClientScripts | null} */
    this.scripts = null;
    /** @type {Set<PipeEntry>} */
    this.pipes = new Set();
    /**
     * Pipes `pipe.hold` steps published, by name, and the `pipe.accept` requests waiting for one.
     * @type {Map<string, { ready: PipeEntry[], waiters: ((pipe: PipeEntry) => void)[] }>}
     */
    this.held = new Map();
    /** Collections enabled by `list.enable`. @type {Set<string>} */
    this.enabled = new Set();
    /** @type {Map<string, { list: EventTarget, listener: (e: Event) => void }>} */
    this.watches = new Map();
    /** @type {number | null} */
    this.lastReason = null;
  }

  get proxy() {
    try {
      return this.client.proxy;
    } catch {
      throw new HostError('Disconnected', NOT_CONNECTED);
    }
  }
}

/** A rental of a pool (§6.11). */
class RentalEntry extends InvokerEntry {
  /**
   * @param {string} h
   * @param {Scope} scope
   * @param {PoolEntry} pool
   * @param {any} rental
   * @param {any} client The pooled client.
   */
  constructor(h, scope, pool, rental, client) {
    super('rental', h, scope, pool.exp);
    this.pool = pool;
    this.rental = rental;
    this.client = client;
  }

  get proxy() {
    try {
      return this.rental.proxy;
    } catch {
      throw new HostError('Disconnected', NOT_CONNECTED);
    }
  }
}

/** A pool of `pool.create`. */
class PoolEntry {
  /**
   * @param {string} h
   * @param {Scope} scope
   * @param {import('./types.js').ScaffoldingExport} exp
   * @param {FakeClock | null} fake
   */
  constructor(h, scope, exp, fake) {
    this.kind = 'pool';
    this.h = h;
    this.scope = scope;
    this.exp = exp;
    this.fake = fake;
    /** @type {any} */
    this.pool = null;
    /** Every client the pool created. @type {Set<any>} */
    this.clients = new Set();
    /** @type {WeakMap<object, string>} */
    this.ids = new WeakMap();
    /** @type {WeakMap<object, number>} */
    this.reasons = new WeakMap();
    this.created = 0;
    /** @type {Map<string, RentalEntry>} */
    this.rentals = new Map();
    this.disposed = false;
  }
}

/** A pipe of `pipe.create`. Exported for tests. */
export class PipeEntry {
  /**
   * @param {string} h
   * @param {Scope} scope
   * @param {ClientEntry} owner
   * @param {any} pipe
   */
  constructor(h, scope, owner, pipe) {
    this.kind = 'pipe';
    this.h = h;
    this.scope = scope;
    this.owner = owner;
    /** @type {import('@dtronix/nexnet').NexusDuplexPipe} */
    this.pipe = pipe;
    this.complete = false;
    this.disposed = false;
    /**
     * Set for a pipe a `pipe.hold` step holds (a pipe the server opened): disposing the entry calls it, which ends the
     * step, instead of disposing the pipe. @type {(() => void) | null}
     */
    this.release = null;
    /** @type {WritableStreamDefaultWriter<Uint8Array> | null} */
    this.byteWriter = null;
    /** @type {ReadableStreamDefaultReader<Uint8Array> | null} */
    this.byteReader = null;
    /** Bytes read and not yet returned. @type {Uint8Array[]} */
    this.buffered = [];
    this.bufferedLength = 0;
    this.inputDone = false;
    /** Channel views of the pipe by item type: a pipe can read one type and write another. @type {Map<string, NexusDuplexChannel<any>>} */
    this.channels = new Map();
    /** @type {any} */
    this.channelWriter = null;
    /** @type {import('./types.js').TypedCodec | null} */
    this.writerCodec = null;
    /** @type {any} */
    this.channelReader = null;
    /** @type {import('./types.js').TypedCodec | null} */
    this.readerCodec = null;
    pipe.completed.then(
      () => (this.complete = true),
      () => (this.complete = true),
    );
  }

  /** @returns {WritableStreamDefaultWriter<Uint8Array>} */
  writer() {
    this.byteWriter ??= this.pipe.writable.getWriter();
    return this.byteWriter;
  }

  /** @returns {ReadableStreamDefaultReader<Uint8Array>} */
  reader() {
    this.byteReader ??= this.pipe.readable.getReader();
    return this.byteReader;
  }

  /**
   * The channel view of the pipe for an item type (one per type). The reader and the writer of a pipe can use
   * different item types, as in C# (`GetChannelReaderAsync<int>` with `GetChannelWriterAsync<string>`).
   * @param {import('./types.js').TypedCodec} codec
   * @returns {NexusDuplexChannel<any>}
   */
  channelFor(codec) {
    let channel = this.channels.get(codec.expression);
    if (!channel) {
      channel = new NexusDuplexChannel(this.pipe, codec.formatter);
      this.channels.set(codec.expression, channel);
    }
    return channel;
  }

  /**
   * Takes up to `max` buffered bytes.
   * @param {number} max
   * @returns {Uint8Array}
   */
  take(max) {
    const length = Math.min(max, this.bufferedLength);
    const out = new Uint8Array(length);
    let offset = 0;
    while (offset < length) {
      const head = this.buffered[0];
      const n = Math.min(head.length, length - offset);
      out.set(head.subarray(0, n), offset);
      offset += n;
      if (n === head.length) this.buffered.shift();
      else this.buffered[0] = head.subarray(n);
    }
    this.bufferedLength -= length;
    return out;
  }
}

/** A future of a detached invocation (§6.5). */
class FutureEntry {
  /**
   * @param {string} h
   * @param {Scope} scope
   * @param {Promise<unknown>} promise
   */
  constructor(h, scope, promise) {
    this.kind = 'future';
    this.h = h;
    this.scope = scope;
    this.done = false;
    this.ok = false;
    /** @type {unknown} */
    this.result = null;
    /** @type {unknown} */
    this.error = null;
    /** @type {Promise<void>} */
    this.settled = promise.then(
      (result) => {
        this.done = true;
        this.ok = true;
        this.result = result ?? null;
      },
      (error) => {
        this.done = true;
        this.error = error;
      },
    );
  }
}

/** @typedef {ClientEntry | RentalEntry | PoolEntry | PipeEntry | FutureEntry} Entry */

/**
 * The parts of a client's session snapshot (`NexusClient.getSnapshot()`) that `client.budget` and
 * `client.outstanding` read (§6.4).
 * @typedef {object} SnapshotPart
 * @property {{ limit: number, reserved: number }} budget
 * @property {{ awaitingResults: number }} invocations
 */

/**
 * The session snapshot of a client, or null when it has no session.
 * @param {{ getSnapshot(): object | null }} client
 * @returns {SnapshotPart | null}
 */
function snapshotOf(client) {
  return /** @type {SnapshotPart | null} */ (client.getSnapshot());
}

/**
 * The C# `NexusCollectionChangedAction` event of a runtime `NexusListChange` (§7.6).
 * @param {string} watch
 * @param {any} change A runtime `NexusListChange`.
 * @param {import('./types.js').TypedCodec} codec
 * @returns {Record<string, unknown>}
 */
export function listChangedData(watch, change, codec) {
  /** @type {Record<string, unknown>} */
  const data = { watch };
  const json = (/** @type {unknown} */ item) => codec.toJson(item);
  switch (change.action) {
    case 'add':
      data.action = 'Add';
      data.index = change.index;
      data.items = [json(change.item)];
      break;
    case 'remove':
      data.action = 'Remove';
      data.oldIndex = change.index;
      data.oldItems = [json(change.item)];
      break;
    case 'replace':
      data.action = 'Replace';
      data.index = change.index;
      data.items = [json(change.item)];
      data.oldItems = [json(change.oldItem)];
      break;
    case 'move':
      data.action = 'Move';
      data.index = change.toIndex;
      data.oldIndex = change.fromIndex;
      data.items = [json(change.item)];
      break;
    case 'ready':
      data.action = 'Ready';
      break;
    default:
      data.action = 'Reset';
      break;
  }
  for (const name of Object.keys(data)) {
    if (data[name] === undefined) delete data[name];
  }
  return data;
}

/** The dispatcher of one control connection. */
export class Host {
  /** @type {Platform} */
  #platform;
  /** @type {import('./types.js').Generated} */
  #generated;
  /** @type {Map<string, Scope>} */
  #scopes = new Map();
  /** @type {Map<string, Entry>} */
  #handles = new Map();
  /** @type {Map<number, { entry: ClientEntry, resolve: (value: unknown) => void, reject: (error: unknown) => void }>} */
  #forwards = new Map();
  /** @type {Map<number, { op: string, test: string | null, started: number }>} */
  #inflight = new Map();
  /** @type {Map<string, (args: Record<string, any>, ctx: Context) => unknown>} */
  #ops = new Map();
  #nextHandle = 1;
  #nextCallId = 1;
  #hostLevel = levelOrder('information');
  #closed = false;

  /**
   * @param {Platform} platform
   * @param {import('./types.js').Generated} generated
   */
  constructor(platform, generated) {
    this.#platform = platform;
    this.#generated = generated;
    this.#registerOps();
  }

  /** Sends `hello` (§4.1); call once the control connection is open. */
  start() {
    /** @type {Record<string, unknown>} */
    const hello = {
      t: 'hello',
      proto: PROTOCOL_VERSION,
      token: this.#platform.token,
      lang: this.#platform.lang,
      runtime: this.#platform.runtime,
      lib: this.#platform.lib,
      schemaHash: this.#generated.schemaHash,
      capabilities: [...new Set([...this.#platform.capabilities, ...RUNTIME_CAPABILITIES])],
      transports: [...this.#platform.transports],
      maxConcurrency: this.#platform.maxConcurrency,
    };
    if (this.#platform.pid !== undefined) hello.pid = this.#platform.pid;
    this.#send(hello);
  }

  /**
   * Handles one message from the runner, synchronously up to the first wait of its op (§6.1).
   * @param {Record<string, any>} message
   */
  receive(message) {
    if (this.#closed) return;
    switch (message.t) {
      case 'req':
        this.#dispatch(message);
        break;
      case 'bye':
        this.shutdown(null, 0);
        break;
      default:
        this.fail(new ControlProtocolError('Unknown control message type ' + JSON.stringify(message.t) + '.'));
        break;
    }
  }

  /**
   * Fails the connection: `bye` with the reason, close, exit.
   * @param {unknown} error
   */
  fail(error) {
    this.shutdown(messageOf(error), 1);
  }

  /**
   * Reports an error no request owns (§7.5).
   * @param {unknown} error
   */
  hostError(error) {
    // A throwing list.watch listener (§6.10) reaches the platform's uncaught-error path by design.
    if (error instanceof ThrowingWatchError) return;
    this.#send({
      t: 'evt',
      test: null,
      h: null,
      ev: 'host.error',
      data: { message: messageOf(error), detail: { stack: error instanceof Error ? error.stack ?? null : null } },
      hostNs: this.#hostNs(),
    });
  }

  /**
   * Ends the host: `bye` (when a reason is given), releases every scope, closes and exits.
   * @param {string | null} reason
   * @param {number} code
   */
  shutdown(reason, code) {
    if (this.#closed) return;
    if (reason !== null) this.#send({ t: 'bye', reason });
    this.#closed = true;
    for (const scope of this.#scopes.values()) scope.controller.abort();
    try {
      this.#platform.close();
    } finally {
      this.#platform.exit(code);
    }
  }

  /** The connection closed under the host. */
  closed() {
    if (this.#closed) return;
    this.#closed = true;
    for (const scope of this.#scopes.values()) scope.controller.abort();
    this.#platform.exit(0);
  }

  /** What the host is doing (`host.dumpThreads`). @returns {string} */
  describe() {
    const lines = ['requests in flight: ' + this.#inflight.size];
    const now = performance.now();
    for (const [id, request] of this.#inflight) {
      lines.push('  #' + id + ' ' + request.op + (request.test ? ' (' + request.test + ')' : '') + ' for ' + Math.round(now - request.started) + ' ms');
    }
    lines.push('scopes: ' + [...this.#scopes.keys()].join(', '));
    for (const entry of this.#handles.values()) {
      if (entry instanceof ClientEntry) {
        lines.push('  client ' + entry.h + ' (' + entry.scope.id + '): ' + stateName(entry.client.state) + ', ' + entry.pending + ' pending, ' + entry.pipes.size + ' pipes');
      }
    }
    lines.push('forwarded calls waiting: ' + this.#forwards.size);
    return lines.join('\n');
  }

  // ----- Plumbing -----

  /** @returns {number} */
  #hostNs() {
    return Math.round(performance.now() * 1e6);
  }

  /** @param {Record<string, unknown>} message */
  #send(message) {
    if (this.#closed) return;
    /** @type {Uint8Array} */
    let frame;
    try {
      frame = encodeFrame(message);
    } catch (error) {
      if (message.t === 'res') {
        frame = encodeFrame({ t: 'res', id: message.id, ok: false, error: toWireError(error), hostNs: this.#hostNs() });
      } else {
        return;
      }
    }
    try {
      this.#platform.send(frame);
    } catch {
      // The connection is gone; closed() follows.
    }
  }

  /**
   * @param {{ scope: Scope, h: string } | null} entry
   * @param {string} ev
   * @param {object} data
   */
  #event(entry, ev, data) {
    this.#send({ t: 'evt', test: entry ? entry.scope.id : null, h: entry ? entry.h : null, ev, data, hostNs: this.#hostNs() });
  }

  /**
   * @param {number} id
   * @param {unknown} value
   */
  #respond(id, value) {
    this.#send({ t: 'res', id, ok: true, value: value ?? {}, hostNs: this.#hostNs() });
  }

  /**
   * @param {number} id
   * @param {unknown} error
   */
  #respondError(id, error) {
    this.#send({ t: 'res', id, ok: false, error: toWireError(error), hostNs: this.#hostNs() });
  }

  /** @param {Record<string, any>} req */
  #dispatch(req) {
    const id = req.id;
    if (typeof id !== 'number') {
      this.fail(new ControlProtocolError('A request without an id.'));
      return;
    }
    const op = req.op;
    if (op === 'ping') {
      this.#respond(id, {});
      return;
    }
    const handler = typeof op === 'string' ? this.#ops.get(op) : undefined;
    if (!handler) {
      this.#respondError(id, badRequest('Unknown op ' + JSON.stringify(op) + '.'));
      return;
    }
    const args = req.args ?? {};
    if (typeof args !== 'object' || args === null || Array.isArray(args)) {
      this.#respondError(id, badRequest('args is not an object.'));
      return;
    }
    const test = typeof req.test === 'string' ? req.test : null;
    /** @type {Scope | null} */
    let scope = null;
    if (!HOST_OPS.has(op)) {
      if (test === null) {
        this.#respondError(id, badRequest(op + ' needs a test scope.'));
        return;
      }
      if (op !== 'test.begin') {
        scope = this.#scopes.get(test) ?? null;
        if (scope === null) {
          this.#respondError(id, badRequest('The test scope ' + test + ' is not open.'));
          return;
        }
      }
    }

    const controller = new AbortController();
    /** @type {Context} */
    const ctx = { signal: controller.signal, scope, test };
    /** @type {unknown} */
    let result;
    try {
      result = handler(args, ctx);
    } catch (error) {
      this.#respondError(id, error);
      return;
    }
    if (!(result instanceof Promise)) {
      this.#respond(id, result);
      return;
    }

    let done = false;
    /** @type {ReturnType<typeof setTimeout> | null} */
    let timer = null;
    const scopeSignal = scope && op !== 'test.end' ? scope.controller.signal : null;
    /**
     * @param {unknown} error
     * @param {unknown} value
     * @param {boolean} failed
     */
    const finish = (failed, error, value) => {
      if (done) return;
      done = true;
      if (timer !== null) globalThis.clearTimeout(timer);
      scopeSignal?.removeEventListener('abort', onScopeEnd);
      this.#inflight.delete(id);
      controller.abort(new HostError('Canceled', 'The request ended.'));
      if (failed) this.#respondError(id, error);
      else this.#respond(id, value);
    };
    const onScopeEnd = () => finish(true, new HostError('Canceled', 'The test scope ended.'), undefined);
    scopeSignal?.addEventListener('abort', onScopeEnd, { once: true });
    const timeoutMs = req.timeoutMs;
    if (typeof timeoutMs === 'number' && timeoutMs > 0) {
      timer = globalThis.setTimeout(
        () => finish(true, new HostError('Timeout', op + ' did not complete within ' + timeoutMs + ' ms.'), undefined),
        timeoutMs,
      );
    }
    this.#inflight.set(id, { op, test, started: performance.now() });
    result.then(
      (value) => finish(false, null, value),
      (error) => finish(true, error ?? new Error(op + ' failed.'), undefined),
    );
  }

  /** @param {string} prefix @returns {string} */
  #newHandle(prefix) {
    return prefix + this.#nextHandle++;
  }

  /**
   * @param {Scope} scope
   * @param {Entry} entry
   */
  #register(scope, entry) {
    this.#handles.set(entry.h, entry);
    scope.handles.add(entry.h);
  }

  /** @param {Entry} entry */
  #release(entry) {
    this.#handles.delete(entry.h);
    entry.scope.handles.delete(entry.h);
  }

  /**
   * @param {Context} ctx
   * @returns {Scope}
   */
  #scope(ctx) {
    if (!ctx.scope) throw badRequest('No test scope.');
    return ctx.scope;
  }

  /**
   * @param {Context} ctx
   * @param {unknown} h
   * @returns {Entry}
   */
  #lookup(ctx, h) {
    const handle = requireString(h, 'handle');
    const entry = this.#handles.get(handle);
    if (!entry || entry.scope !== ctx.scope) throw unknownHandle('Unknown handle ' + handle + ' in test scope ' + ctx.test + '.');
    return entry;
  }

  /**
   * A client handle (not a rental).
   * @param {Context} ctx
   * @param {unknown} h
   * @param {string} op
   * @returns {ClientEntry}
   */
  #client(ctx, h, op) {
    const entry = this.#lookup(ctx, h);
    if (entry instanceof ClientEntry) return entry;
    if (entry instanceof RentalEntry) throw notSupported(op + ' is not supported on a pooled client: the pool owns the connection.');
    throw unknownHandle(String(h) + ' is not a client handle.');
  }

  /**
   * A client or rental handle.
   * @param {Context} ctx
   * @param {unknown} h
   * @returns {ClientEntry | RentalEntry}
   */
  #invoker(ctx, h) {
    const entry = this.#lookup(ctx, h);
    if (entry instanceof ClientEntry || entry instanceof RentalEntry) return entry;
    throw unknownHandle(String(h) + ' is not a client handle.');
  }

  /**
   * @param {Context} ctx
   * @param {unknown} h
   * @returns {PipeEntry}
   */
  #pipe(ctx, h) {
    const entry = this.#lookup(ctx, h);
    if (entry instanceof PipeEntry) return entry;
    throw unknownHandle(String(h) + ' is not a pipe handle.');
  }

  /**
   * @param {Context} ctx
   * @param {unknown} h
   * @returns {PoolEntry}
   */
  #poolEntry(ctx, h) {
    const entry = this.#lookup(ctx, h);
    if (entry instanceof PoolEntry) return entry;
    throw unknownHandle(String(h) + ' is not a pool handle.');
  }

  /**
   * The export of a `{client, server}` nexus pair.
   * @param {unknown} nexus
   * @returns {import('./types.js').ScaffoldingExport}
   */
  #export(nexus) {
    const pair = /** @type {Record<string, unknown>} */ (nexus ?? {});
    const client = requireString(pair.client, 'nexus.client');
    const server = requireString(pair.server, 'nexus.server');
    const exp = this.#generated.find(client, server);
    if (!exp) throw notSupported('This host has no generated code for the nexus pair ' + client + ' / ' + server + '.');
    return exp;
  }

  /**
   * `pipe.hold` (§6.9): registers the pipe the server passed to a client nexus method as a pipe handle of the client,
   * publishes it for `pipe.accept`, and resolves when the handle is disposed.
   * @param {ClientEntry} entry
   * @param {string} name
   * @param {import('@dtronix/nexnet').NexusDuplexPipe} pipe
   * @returns {Promise<void>}
   */
  #hold(entry, name, pipe) {
    const pe = new PipeEntry(this.#newHandle('p'), entry.scope, entry, pipe);
    /** @type {Promise<void>} */
    const released = new Promise((resolve) => {
      pe.release = () => resolve();
    });
    entry.pipes.add(pe);
    this.#register(entry.scope, pe);
    const box = this.#heldBox(entry, name);
    const waiter = box.waiters.shift();
    if (waiter) waiter(pe);
    else box.ready.push(pe);
    return released;
  }

  /**
   * The held pipes of a client published under `name`, and the `pipe.accept` requests waiting for one.
   * @param {ClientEntry} entry
   * @param {string} name
   * @returns {{ ready: PipeEntry[], waiters: ((pipe: PipeEntry) => void)[] }}
   */
  #heldBox(entry, name) {
    let box = entry.held.get(name);
    if (!box) {
      box = { ready: [], waiters: [] };
      entry.held.set(name, box);
    }
    return box;
  }

  /**
   * The signal of a cancellation `{afterMs}` or `{token}` (§6.5); the returned function stops an `afterMs` timer.
   * @param {Context} ctx
   * @param {InvokerEntry} entry
   * @param {unknown} cancel
   * @returns {{ signal: AbortSignal | undefined, start: () => void, stop: () => void }}
   */
  #cancel(ctx, entry, cancel) {
    const none = { signal: undefined, start: () => {}, stop: () => {} };
    if (cancel === undefined || cancel === null) return none;
    const c = /** @type {Record<string, unknown>} */ (cancel);
    if (typeof c.token === 'string') {
      const source = this.#scope(ctx).tokens.get(c.token);
      if (!source) throw unknownHandle('Unknown cancellation token ' + c.token + '.');
      return { signal: source.signal, start: () => {}, stop: () => {} };
    }
    if (typeof c.afterMs === 'number') {
      const source = new AbortController();
      const ms = Math.max(0, c.afterMs);
      let stop = () => {};
      return {
        signal: source.signal,
        start: () => {
          stop = entry.timer(() => source.abort(cancellationError()), ms);
        },
        stop: () => stop(),
      };
    }
    throw badRequest('cancel is neither {afterMs} nor {token}.');
  }

  /**
   * Log events of a client (§7.4).
   * @param {ClientEntry} entry
   * @param {string} minLevel
   * @param {string | null} category
   * @returns {{ log: (level: string, message: string, error?: unknown) => void, child: (path: string) => any }}
   */
  #logger(entry, minLevel, category) {
    const min = levelOrder(minLevel);
    return {
      log: (level, message, error) => {
        if (levelOrder(level) < min) return;
        const text = error === undefined || error === null ? message : message + ' ' + messageOf(error);
        /** @type {Record<string, unknown>} */
        const data = { level: levelName(level), message: text };
        if (category !== null) data.category = category;
        this.#event(entry, 'log', data);
      },
      child: (path) => this.#logger(entry, minLevel, category === null ? path : category + '/' + path),
    };
  }

  /**
   * @param {string} level
   * @param {string} message
   */
  #hostLog(level, message) {
    if (levelOrder(level) < this.#hostLevel) return;
    this.#event(null, 'log', { level: levelName(level), message });
  }

  // ----- Leak audit and release -----

  /**
   * @param {ClientEntry} entry
   * @param {LeakReport} report
   */
  #auditClient(entry, report) {
    const state = entry.client.state;
    const connected = state !== ConnectionState.Disconnected;
    if (!entry.disposed && connected) {
      report.openClients++;
      report.details.push('client ' + entry.h + ' is ' + stateName(state) + ' and not disposed');
    }
    for (const pipe of entry.pipes) {
      if (!pipe.disposed && !pipe.complete) {
        report.openPipes++;
        report.details.push('pipe ' + pipe.h + ' of ' + entry.h + ' neither completed nor disposed');
      }
    }
    const timers = entry.clock.activeTimers + entry.timers.size;
    if (timers > 0) {
      report.timers += timers;
      report.details.push(timers + ' timer(s) of client ' + entry.h + ' (' + stateName(state) + ') still scheduled');
    }
    if (entry.pending > 0) {
      report.pendingInvocations += entry.pending;
      report.details.push(entry.pending + ' invocation(s) of ' + entry.h + ' still running');
    }
    // Watches only, as the C# reference driver (InProcClientDriver) counts them: an enabled collection of a connected
    // client is part of an open client, and a disconnected client has none enabled.
    const subscriptions = entry.watches.size;
    if (subscriptions > 0) report.details.push(subscriptions + ' collection watch(es) of ' + entry.h + ' not ended');
    report.subscriptions += subscriptions;
  }

  /**
   * @param {PoolEntry} entry
   * @param {LeakReport} report
   */
  #auditPool(entry, report) {
    let id = 0;
    for (const client of entry.clients) {
      id++;
      if (client.state !== ConnectionState.Disconnected) {
        report.openClients++;
        const rented = [...entry.rentals.values()].some((r) => r.client === client);
        report.details.push('pooled client ' + (entry.ids.get(client) ?? '#' + id) + ' of ' + entry.h + ' is ' + stateName(client.state)
          + (rented ? ' and rented, not returned' : ', idle in a pool not disposed'));
      }
    }
    if (!entry.disposed) {
      report.timers++;
      report.details.push('the health check of pool ' + entry.h + ' runs: the pool is not disposed');
    }
    for (const rental of entry.rentals.values()) {
      if (rental.pending > 0 || rental.timers.size > 0) {
        report.pendingInvocations += rental.pending;
        report.timers += rental.timers.size;
        report.details.push(rental.pending + ' invocation(s) of rental ' + rental.h + ' still running');
      }
    }
  }

  /**
   * The leak report of a scope, a client or a pool, after waiting up to 1 s for it to settle (§5.1).
   * @param {() => LeakReport} compute
   * @returns {Promise<LeakReport>}
   */
  async #settledReport(compute) {
    const deadline = performance.now() + AUDIT_SETTLE_MS;
    for (;;) {
      const report = compute();
      if (isClean(report) || performance.now() >= deadline) return report;
      await sleep(20);
    }
  }

  /**
   * @param {Scope} scope
   * @returns {LeakReport}
   */
  #scopeReport(scope) {
    const report = emptyReport();
    for (const h of scope.handles) {
      const entry = this.#handles.get(h);
      if (entry instanceof ClientEntry) this.#auditClient(entry, report);
      else if (entry instanceof PoolEntry) this.#auditPool(entry, report);
    }
    return report;
  }

  /**
   * @param {ClientEntry} entry
   * @returns {Promise<void>}
   */
  async #disposeClient(entry) {
    if (entry.disposed) return;
    entry.disposed = true;
    entry.lifetime.abort(cancellationError());
    for (const watch of entry.watches.values()) watch.list.removeEventListener('changed', watch.listener);
    entry.watches.clear();
    for (const pipe of [...entry.pipes]) await this.#disposePipe(pipe);
    for (const forward of [...this.#forwards]) {
      if (forward[1].entry === entry) {
        this.#forwards.delete(forward[0]);
        forward[1].reject(cancellationError());
      }
    }
    try {
      entry.config.disconnectDelay = 0;
    } catch {
      // A frozen config: keep the default.
    }
    try {
      await entry.client[Symbol.asyncDispose]();
    } catch (error) {
      this.#hostLog('warning', 'Disposing client ' + entry.h + ' failed: ' + messageOf(error));
    }
    this.#release(entry);
  }

  /**
   * @param {PipeEntry} entry
   * @returns {Promise<void>}
   */
  async #disposePipe(entry) {
    if (entry.disposed) return;
    entry.disposed = true;
    if (entry.release) {
      // A held pipe belongs to its method: the runtime releases it when the method returns.
      entry.release();
    } else {
      try {
        await entry.pipe[Symbol.asyncDispose]();
      } catch {
        // A pipe of an ended session.
      }
    }
    this.#release(entry);
  }

  /**
   * Releases everything a scope holds (`test.end`).
   * @param {Scope} scope
   * @returns {Promise<void>}
   */
  async #releaseScope(scope) {
    for (const token of scope.tokens.values()) token.abort(cancellationError());
    scope.tokens.clear();
    const entries = [...scope.handles].map((h) => this.#handles.get(h)).filter((e) => e !== undefined);
    /** @param {Promise<unknown>} work */
    const bounded = (work) => Promise.race([work.catch(() => {}), sleep(5000)]);
    // Rentals go back first, then the pools, then the clients.
    for (const entry of entries) {
      if (entry instanceof PoolEntry) {
        for (const rental of [...entry.rentals.values()]) this.#returnRental(entry, rental);
      }
    }
    await Promise.all(entries.filter((e) => e instanceof PoolEntry).map((e) => bounded(this.#disposePool(/** @type {PoolEntry} */ (e)))));
    await Promise.all(entries.filter((e) => e instanceof ClientEntry).map((e) => bounded(this.#disposeClient(/** @type {ClientEntry} */ (e)))));
    for (const h of [...scope.handles]) this.#handles.delete(h);
    scope.handles.clear();
  }

  /**
   * @param {PoolEntry} entry
   * @param {RentalEntry} rental
   */
  #returnRental(entry, rental) {
    if (rental.disposed) return;
    rental.disposed = true;
    entry.rentals.delete(rental.h);
    try {
      rental.rental.release();
    } catch {
      // Released already.
    }
    this.#release(rental);
  }

  /**
   * @param {PoolEntry} entry
   * @returns {Promise<void>}
   */
  async #disposePool(entry) {
    if (entry.disposed) return;
    entry.disposed = true;
    entry.pool.config.clientConfig.disconnectDelay = 0;
    await entry.pool[Symbol.asyncDispose]();
  }

  // ----- Waits -----

  /**
   * Waits until the client's state is `value`.
   * @param {any} client
   * @param {number} value
   * @param {AbortSignal} signal
   * @returns {Promise<void>}
   */
  #awaitState(client, value, signal) {
    if (client.state === value) return Promise.resolve();
    return new Promise((resolve, reject) => {
      const onChange = (/** @type {Event} */ event) => {
        const detail = /** @type {CustomEvent} */ (event).detail;
        if (client.state === value || detail?.state === value) {
          cleanup();
          resolve();
        }
      };
      const onAbort = () => {
        cleanup();
        reject(signal.reason);
      };
      const cleanup = () => {
        client.removeEventListener('statechange', onChange);
        signal.removeEventListener('abort', onAbort);
      };
      client.addEventListener('statechange', onChange);
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }

  /**
   * Waits until the current session has ended (C# `DisconnectedTask`): at once when the client is disconnected or
   * between reconnect attempts, else at its next `disconnected` event or when it reaches `Disconnected` or
   * `Reconnecting`. (The runtime's `client.disconnected` resolves only once reconnection gives up.)
   * @param {any} client
   * @param {AbortSignal} signal
   * @returns {Promise<void>}
   */
  #awaitSessionEnd(client, signal) {
    const ended = () => client.state === ConnectionState.Disconnected || client.state === ConnectionState.Reconnecting;
    if (ended()) return Promise.resolve();
    return new Promise((resolve, reject) => {
      const onDisconnected = () => {
        cleanup();
        resolve();
      };
      const onState = () => {
        if (!ended()) return;
        cleanup();
        resolve();
      };
      const onAbort = () => {
        cleanup();
        reject(signal.reason);
      };
      const cleanup = () => {
        client.removeEventListener('disconnected', onDisconnected);
        client.removeEventListener('statechange', onState);
        signal.removeEventListener('abort', onAbort);
      };
      client.addEventListener('disconnected', onDisconnected);
      client.addEventListener('statechange', onState);
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }

  /**
   * Polls a rental's state (a rental raises no events).
   * @param {RentalEntry} entry
   * @param {number} value
   * @param {AbortSignal} signal
   * @returns {Promise<void>}
   */
  async #pollState(entry, value, signal) {
    while (entry.rental.state !== value) {
      if (signal.aborted) throw signal.reason;
      await sleep(10);
    }
  }

  // ----- Ops -----

  #registerOps() {
    /**
     * @param {string} name
     * @param {(args: Record<string, any>, ctx: Context) => unknown} handler
     */
    const op = (name, handler) => this.#ops.set(name, handler);

    // Host level (§6.2).
    op('host.dumpThreads', () => ({ text: this.#platform.dumpThreads() + '\n\n' + this.describe() }));
    op('host.shutdown', () => {
      queueMicrotask(() => this.shutdown('host.shutdown', 0));
      return {};
    });
    op('log.level', (args) => {
      const level = runtimeLevel(args.level);
      if (level === undefined) throw badRequest('Unknown log level ' + JSON.stringify(args.level) + '.');
      this.#hostLevel = levelOrder(level);
      return {};
    });

    // Test scopes (§6.3).
    op('test.begin', (args, ctx) => {
      const test = /** @type {string} */ (ctx.test);
      if (!this.#scopes.has(test)) this.#scopes.set(test, new Scope(test));
      return {};
    });
    op('test.audit', (args, ctx) => {
      const scope = this.#scope(ctx);
      if (args.pool !== undefined && args.pool !== null) {
        const pool = this.#poolEntry(ctx, args.pool);
        return this.#settledReport(() => {
          const report = emptyReport();
          this.#auditPool(pool, report);
          return report;
        });
      }
      if (args.h !== undefined && args.h !== null) {
        const entry = this.#invoker(ctx, args.h);
        if (entry instanceof RentalEntry) {
          return this.#settledReport(() => {
            const report = emptyReport();
            report.pendingInvocations = entry.pending;
            report.timers = entry.timers.size;
            if (entry.pending > 0) report.details.push(entry.pending + ' invocation(s) still running');
            return report;
          });
        }
        return this.#settledReport(() => {
          const report = emptyReport();
          this.#auditClient(entry, report);
          return report;
        });
      }
      return this.#settledReport(() => this.#scopeReport(scope));
    });
    op('test.end', async (args, ctx) => {
      const scope = this.#scope(ctx);
      const report = await this.#settledReport(() => this.#scopeReport(scope));
      this.#scopes.delete(scope.id);
      scope.controller.abort(new HostError('Canceled', 'The test scope ended.'));
      await this.#releaseScope(scope);
      return report;
    });

    // Clients (§6.4).
    op('client.create', (args, ctx) => {
      const scope = this.#scope(ctx);
      const exp = this.#export(args.nexus);
      const parsed = parseSpec(args.spec, { transports: this.#platform.transports, browser: this.#platform.browser });
      const fake = parsed.fakeTime ? new FakeClock() : null;
      const entry = new ClientEntry(this.#newHandle('c'), scope, exp, new TrackingClock(fake ?? systemClock), fake);
      entry.config = { ...parsed.config, clock: entry.clock, logger: parsed.logLevel ? this.#logger(entry, parsed.logLevel, null) : null };
      entry.scripts = new ClientScripts({
        event: (ev, data) => this.#event(entry, ev, data),
        forward: (slot, callArgs, signal) => this.#forward(entry, slot, callArgs, signal),
        invoke: (method, callArgs) => entry.exp.invokeByName(entry.client.proxy, method, callArgs),
        timer: (callback, ms) => entry.timer(callback, ms),
        lifetime: entry.lifetime.signal,
        hold: (name, pipe) => this.#hold(entry, name, pipe),
      }, { invocationError: parsed.onInvocationException });
      const client = exp.createClient(entry.config, entry.scripts);
      entry.client = client;
      client.addEventListener('statechange', (/** @type {Event} */ event) => {
        const detail = /** @type {CustomEvent} */ (event).detail;
        this.#event(entry, 'client.state', { state: stateName(detail?.state ?? client.state) });
      });
      client.addEventListener('disconnected', (/** @type {Event} */ event) => {
        entry.lastReason = /** @type {CustomEvent} */ (event).detail?.reason ?? null;
      });
      this.#register(scope, entry);
      return { h: entry.h };
    });
    op('client.connect', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'client.connect');
      try {
        await entry.client.connect();
      } catch (error) {
        // A connect that fails before the handshake raises no disconnected event; its reason is the error's (as the
        // C# DisconnectedTask reports it).
        const reason = /** @type {any} */ (error)?.reason;
        if (typeof reason === 'number') entry.lastReason = reason;
        throw error;
      }
      return {};
    });
    op('client.tryConnect', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'client.tryConnect');
      const result = await entry.client.tryConnect();
      if (!result.success) entry.lastReason = result.disconnectReason;
      return { ok: result.success === true };
    });
    op('client.disconnect', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'client.disconnect');
      await entry.client.disconnect();
      await this.#awaitState(entry.client, ConnectionState.Disconnected, ctx.signal);
      return {};
    });
    op('client.dispose', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'client.dispose');
      await this.#disposeClient(entry);
      return {};
    });
    op('client.state', (args, ctx) => {
      const entry = this.#invoker(ctx, args.h);
      const state = entry instanceof RentalEntry ? entry.rental.state : entry.client.state;
      return { state: stateName(state) };
    });
    op('client.remoteCertificate', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'client.remoteCertificate');
      // Node's X509Certificate on the Node TLS transports; null otherwise (and always in the browser).
      const cert = /** @type {{ raw?: Uint8Array } | null} */ (entry.client.state === ConnectionState.Connected ? entry.client.remoteCertificate : null);
      return { der: cert?.raw ? toBase64(cert.raw) : null };
    });
    op('client.isSecure', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'client.isSecure');
      return { secure: entry.client.isSecure };
    });
    op('client.remoteEndpoint', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'client.remoteEndpoint');
      // The peer of the Node socket; null in the browser, which does not expose it.
      if (entry.client.state !== ConnectionState.Connected) return { address: null, port: null };
      return { address: entry.client.remoteAddress, port: entry.client.remotePort };
    });
    op('client.budget', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'client.budget');
      const budget = snapshotOf(entry.client)?.budget ?? null;
      return { budget: budget ? { limit: budget.limit, reserved: budget.reserved } : null };
    });
    op('client.outstanding', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'client.outstanding');
      return { invocations: snapshotOf(entry.client)?.invocations.awaitingResults ?? 0 };
    });
    op('client.awaitState', async (args, ctx) => {
      const entry = this.#invoker(ctx, args.h);
      const value = stateValue(requireString(args.state, 'state'));
      if (value === undefined) throw badRequest('Unknown ConnectionState ' + JSON.stringify(args.state) + '.');
      if (entry instanceof RentalEntry) await this.#pollState(entry, value, ctx.signal);
      else await this.#awaitState(entry.client, value, ctx.signal);
      return {};
    });
    op('client.awaitReady', async (args, ctx) => {
      const entry = this.#invoker(ctx, args.h);
      if (entry instanceof RentalEntry) await this.#pollState(entry, ConnectionState.Connected, ctx.signal);
      else await this.#awaitState(entry.client, ConnectionState.Connected, ctx.signal);
      return {};
    });
    op('client.awaitDisconnected', async (args, ctx) => {
      const entry = this.#invoker(ctx, args.h);
      if (entry instanceof RentalEntry) {
        if (entry.rental.state !== ConnectionState.Disconnected) await abortable(entry.rental.disconnected, ctx.signal);
        return { reason: reasonName(entry.pool.reasons.get(entry.client) ?? null) };
      }
      await this.#awaitSessionEnd(entry.client, ctx.signal);
      return { reason: reasonName(entry.lastReason) };
    });
    op('time.advance', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'time.advance');
      const ms = requireInt(args.ms, 'ms', 0);
      if (!entry.fake) throw invalidOperation('Client ' + entry.h + ' was not created with spec.fakeTime.');
      entry.fake.advance(ms);
      await sleep(0);
      return {};
    });

    // Invocations (§6.5).
    op('invoke', (args, ctx) => this.#invoke(args, ctx));
    op('future.await', async (args, ctx) => {
      const entry = this.#lookup(ctx, args.future);
      if (!(entry instanceof FutureEntry)) throw unknownHandle(String(args.future) + ' is not a future.');
      await abortable(entry.settled, ctx.signal);
      this.#release(entry);
      if (!entry.ok) throw entry.error;
      return { result: entry.result };
    });
    op('future.poll', (args, ctx) => {
      const entry = this.#lookup(ctx, args.future);
      if (!(entry instanceof FutureEntry)) throw unknownHandle(String(args.future) + ' is not a future.');
      if (!entry.done) return { done: false };
      this.#release(entry);
      return entry.ok ? { done: true, ok: true, result: entry.result } : { done: true, ok: false, error: toWireError(entry.error) };
    });
    op('token.create', (args, ctx) => {
      const scope = this.#scope(ctx);
      const name = requireString(args.token, 'token');
      if (scope.tokens.has(name)) throw badRequest('The token ' + name + ' exists.');
      scope.tokens.set(name, new AbortController());
      return {};
    });
    op('token.cancel', (args, ctx) => {
      const name = requireString(args.token, 'token');
      this.#scope(ctx).tokens.get(name)?.abort(cancellationError());
      return {};
    });
    op('token.dispose', (args, ctx) => {
      this.#scope(ctx).tokens.delete(requireString(args.token, 'token'));
      return {};
    });

    // Scripts (§6.6).
    op('script.set', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'script.set');
      const slot = requireString(args.slot, 'slot');
      if (!entry.exp.slots.includes(slot)) throw badRequest('The client nexus has no slot ' + slot + '.');
      const steps = validateSteps(args.steps);
      /** @type {number | null} */
      let times = null;
      if (args.times !== undefined && args.times !== null) times = requireInt(args.times, 'times', 1);
      /** @type {ClientScripts} */ (entry.scripts).set(slot, steps, times);
      return {};
    });
    op('script.clear', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'script.clear');
      /** @type {ClientScripts} */ (entry.scripts).clear(requireString(args.slot, 'slot'));
      return {};
    });
    op('signal', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'signal');
      /** @type {ClientScripts} */ (entry.scripts).raise(requireString(args.name, 'name'));
      return {};
    });
    op('callback.reply', (args) => {
      const callId = args.callId;
      const forward = typeof callId === 'number' ? this.#forwards.get(callId) : undefined;
      if (!forward) return {}; // The call is over.
      this.#forwards.delete(/** @type {number} */ (callId));
      if (args.error !== undefined && args.error !== null) {
        forward.reject(nativeError(String(args.error.kind ?? 'InvalidOperation'), args.error.message ?? null));
      } else {
        forward.resolve(args.result ?? null);
      }
      return {};
    });

    // Pipes (§6.7).
    op('pipe.create', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'pipe.create');
      /** @type {any} */
      let pipe;
      try {
        pipe = entry.client.createPipe();
      } catch (error) {
        if (error instanceof NexusError || error instanceof TypeError || error instanceof RangeError) throw error;
        throw new HostError('Disconnected', NOT_CONNECTED);
      }
      const pe = new PipeEntry(this.#newHandle('p'), entry.scope, entry, pipe);
      entry.pipes.add(pe);
      this.#register(entry.scope, pe);
      return { pipe: pe.h };
    });
    op('pipe.ready', async (args, ctx) => {
      await this.#pipe(ctx, args.pipe).pipe.ready;
      return {};
    });
    op('pipe.write', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      const writer = pe.writer();
      try {
        if (typeof args.b64 === 'string') {
          await writer.write(fromBase64(args.b64));
        } else if (args.pattern && typeof args.pattern === 'object') {
          const seed = requireInt(args.pattern.seed, 'pattern.seed');
          const length = requireInt(args.pattern.length, 'pattern.length', 0);
          for (let offset = 0; offset < length; offset += PATTERN_CHUNK) {
            await writer.write(pattern(offset, Math.min(PATTERN_CHUNK, length - offset), seed));
          }
        } else {
          throw badRequest('pipe.write needs b64 or pattern.');
        }
      } catch (error) {
        if (error instanceof HostError) throw error;
        throw invalidOperation('The pipe completed before the bytes were written: ' + messageOf(error));
      }
      return {};
    });
    op('pipe.read', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      const min = requireInt(args.min, 'min', 0);
      const max = requireInt(args.max, 'max', 1);
      const reader = pe.reader();
      while (pe.bufferedLength < Math.max(1, min) && !pe.inputDone) {
        const { value, done } = await reader.read();
        if (done) pe.inputDone = true;
        else if (value.length > 0) {
          pe.buffered.push(value);
          pe.bufferedLength += value.length;
        }
      }
      return { b64: toBase64(pe.take(max)) };
    });
    op('pipe.drain', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      const hash = new Sha256();
      let length = 0;
      const leftover = pe.take(pe.bufferedLength);
      hash.update(leftover);
      length += leftover.length;
      const reader = pe.reader();
      while (!pe.inputDone) {
        const { value, done } = await reader.read();
        if (done) pe.inputDone = true;
        else {
          hash.update(value);
          length += value.length;
        }
      }
      return { length, sha256: hash.hex() };
    });
    op('pipe.complete', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      try {
        await pe.writer().close();
      } catch {
        // The writing side is complete already.
      }
      return {};
    });
    op('pipe.whenComplete', async (args, ctx) => {
      await this.#pipe(ctx, args.pipe).pipe.completed;
      return {};
    });
    op('pipe.dispose', async (args, ctx) => {
      const entry = this.#lookup(ctx, args.pipe);
      if (!(entry instanceof PipeEntry)) throw unknownHandle(String(args.pipe) + ' is not a pipe handle.');
      await this.#disposePipe(entry);
      return {};
    });

    op('pipe.accept', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'pipe.accept');
      const box = this.#heldBox(entry, requireString(args.name, 'name'));
      const ready = box.ready.shift();
      if (ready) return { pipe: ready.h };
      const signal = ctx.signal;
      /** @type {PipeEntry} */
      const pe = await new Promise((resolve, reject) => {
        if (signal.aborted) {
          reject(signal.reason);
          return;
        }
        const onAbort = () => {
          const index = box.waiters.indexOf(waiter);
          if (index >= 0) box.waiters.splice(index, 1);
          reject(signal.reason);
        };
        /** @param {PipeEntry} held */
        const waiter = (held) => {
          signal.removeEventListener('abort', onAbort);
          resolve(held);
        };
        box.waiters.push(waiter);
        signal.addEventListener('abort', onAbort, { once: true });
      });
      return { pipe: pe.h };
    });
    op('pipe.tryWrite', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      if (typeof args.b64 !== 'string') throw badRequest('pipe.tryWrite needs b64.');
      const data = fromBase64(args.b64);
      try {
        await pe.writer().write(data);
        return { ok: true };
      } catch {
        // The writable errors once the writing side is complete (the server completed its reading side or the
        // pipe, or the session ended): C# FlushResult.IsCompleted.
        return { ok: false };
      }
    });
    op('pipe.completeInput', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      pe.inputDone = true;
      pe.buffered = [];
      pe.bufferedLength = 0;
      try {
        // Cancelling the readable completes this side's reading direction.
        await (pe.byteReader ? pe.byteReader.cancel() : pe.pipe.readable.cancel());
      } catch {
        // The reading side is complete already.
      }
      return {};
    });

    // Channels (§6.8).
    op('channel.writer', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      const codec = this.#codec(args.type);
      await pe.pipe.ready;
      pe.channelWriter = await pe.channelFor(codec).getWriter();
      pe.writerCodec = codec;
      return {};
    });
    op('channel.write', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      if (!pe.channelWriter || !pe.writerCodec) throw invalidOperation('channel.writer was not called for pipe ' + pe.h + '.');
      if (!Array.isArray(args.items)) throw badRequest('channel.write needs items.');
      const codec = pe.writerCodec;
      const items = args.items.map((/** @type {unknown} */ item) => codec.fromJson(item));
      try {
        await pe.channelWriter.writeMany(items);
        return { ok: true };
      } catch (error) {
        if (!isChannelClosed(error)) throw error;
        return { ok: false };
      }
    });
    op('channel.complete', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      if (!pe.channelWriter) throw invalidOperation('channel.writer was not called for pipe ' + pe.h + '.');
      await pe.channelWriter.complete();
      return {};
    });
    op('channel.reader', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      const codec = this.#codec(args.type);
      pe.channelReader = await pe.channelFor(codec).getReader();
      pe.readerCodec = codec;
      return {};
    });
    op('channel.read', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      if (!pe.channelReader || !pe.readerCodec) throw invalidOperation('channel.reader was not called for pipe ' + pe.h + '.');
      const codec = pe.readerCodec;
      const max = requireInt(args.max, 'max', 1);
      // A canceled read rejects with an AbortError (kind Canceled) and consumes no item.
      const cancel = this.#cancel(ctx, pe.owner, args.cancel);
      cancel.start();
      try {
        const items = await pe.channelReader.readBatch(max, cancel.signal ? { signal: cancel.signal } : undefined);
        return { items: items.map((/** @type {unknown} */ item) => codec.toJson(item)), complete: items.length === 0 };
      } finally {
        cancel.stop();
      }
    });
    op('channel.readAll', async (args, ctx) => {
      const pe = this.#pipe(ctx, args.pipe);
      if (!pe.channelReader || !pe.readerCodec) throw invalidOperation('channel.reader was not called for pipe ' + pe.h + '.');
      const codec = pe.readerCodec;
      const items = await pe.channelReader.readUntilComplete();
      return { items: items.map((/** @type {unknown} */ item) => codec.toJson(item)) };
    });

    // Synchronized collections (§6.10).
    op('list.enable', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'list.enable');
      const { list, name } = this.#list(entry, args.collection);
      const cancel = this.#cancel(ctx, entry, args.cancel);
      cancel.start();
      try {
        const ok = await list.enable({ signal: cancel.signal });
        if (ok) {
          entry.enabled.add(name);
          await (cancel.signal ? abortable(list.ready, cancel.signal) : list.ready);
        }
        return { ok };
      } finally {
        cancel.stop();
      }
    });
    op('list.disable', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'list.disable');
      const { list, name } = this.#list(entry, args.collection);
      await list.disable();
      entry.enabled.delete(name);
      return {};
    });
    op('list.op', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'list.op');
      const { list, codec } = this.#list(entry, args.collection);
      const item = () => codec.fromJson(args.item);
      /** @param {string} name @returns {number} */
      const index = (name) => requireInt(args[name], name);
      /** @type {Promise<boolean>} */
      let result;
      switch (args.op) {
        case 'add':
          result = list.add(item());
          break;
        case 'insert':
          result = list.insert(index('index'), item());
          break;
        case 'remove':
          result = list.remove(item());
          break;
        case 'removeAt':
          result = list.removeAt(index('index'));
          break;
        case 'replace':
          result = list.replace(index('index'), item());
          break;
        case 'move':
          result = list.move(index('from'), index('to'));
          break;
        case 'clear':
          result = list.clear();
          break;
        default:
          throw badRequest('Unknown list op ' + JSON.stringify(args.op) + '.');
      }
      return { ok: (await result) === true };
    });
    op('list.snapshot', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'list.snapshot');
      const { list, codec } = this.#list(entry, args.collection);
      return { items: list.toArray().map((item) => codec.toJson(item)) };
    });
    op('list.state', async (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'list.state');
      const { list } = this.#list(entry, args.collection);
      // Whether the disabled promise has settled: a settled promise's reaction runs before the one queued after it.
      const pending = Symbol('pending');
      let disabled;
      try {
        disabled = (await Promise.race([list.disabled, Promise.resolve(pending)])) !== pending;
      } catch {
        disabled = true;
      }
      // The JS list has no read-only flag (C# IsReadOnly): its mode tells whether the client can change it.
      return { mode: collectionModeName(list.mode), state: collectionStateName(list.state), disabled };
    });
    op('list.watch', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'list.watch');
      const watch = requireString(args.watch, 'watch');
      if (entry.watches.has(watch)) throw badRequest('The watch ' + watch + ' exists.');
      const { list, codec } = this.#list(entry, args.collection);
      if (args.throws !== undefined && args.throws !== null && typeof args.throws !== 'boolean') throw badRequest('throws must be a boolean.');
      const throwing = args.throws === true;
      const listener = (/** @type {Event} */ event) => {
        this.#event(entry, 'list.changed', listChangedData(watch, /** @type {CustomEvent} */ (event).detail, codec));
        if (throwing) throw new ThrowingWatchError();
      };
      list.addEventListener('changed', listener);
      entry.watches.set(watch, { list, listener });
      return {};
    });
    op('list.unwatch', (args, ctx) => {
      const entry = this.#client(ctx, args.h, 'list.unwatch');
      const watch = requireString(args.watch, 'watch');
      const found = entry.watches.get(watch);
      if (!found) throw unknownHandle('Unknown watch ' + watch + '.');
      found.list.removeEventListener('changed', found.listener);
      entry.watches.delete(watch);
      return {};
    });

    // Client pools (§6.11).
    op('pool.create', (args, ctx) => this.#createPool(args, ctx));
    op('pool.rent', (args, ctx) => this.#rent(args, ctx));
    op('pool.return', (args, ctx) => {
      const pool = this.#poolEntry(ctx, args.pool);
      const rental = pool.rentals.get(requireString(args.h, 'h'));
      if (!rental) throw unknownHandle('Unknown rental ' + String(args.h) + ' of pool ' + pool.h + '.');
      this.#returnRental(pool, rental);
      return {};
    });
    op('pool.ensureConnected', async (args, ctx) => {
      const entry = this.#lookup(ctx, args.h);
      if (!(entry instanceof RentalEntry)) throw unknownHandle(String(args.h) + ' is not a rental handle.');
      return { ok: (await entry.rental.ensureConnected()) === true };
    });
    op('pool.state', (args, ctx) => {
      const pool = this.#poolEntry(ctx, args.pool);
      return { maxConnections: pool.pool.maxConnections, idle: pool.pool.availableConnections, rented: pool.rentals.size };
    });
    op('pool.advanceTime', async (args, ctx) => {
      const pool = this.#poolEntry(ctx, args.pool);
      const ms = requireInt(args.ms, 'ms', 0);
      if (!pool.fake) throw invalidOperation('Pool ' + pool.h + ' was not created with spec.fakeTime.');
      pool.fake.advance(ms);
      await sleep(0);
      return {};
    });
    op('pool.dispose', async (args, ctx) => {
      const pool = this.#poolEntry(ctx, args.pool);
      await this.#disposePool(pool);
      return {};
    });
  }

  /**
   * @param {unknown} type
   * @returns {import('./types.js').TypedCodec}
   */
  #codec(type) {
    const expression = requireString(type, 'type');
    const codec = this.#generated.forType(expression);
    if (!codec) throw notSupported('This host has no codec for the type ' + expression + '.');
    return codec;
  }

  /**
   * @param {ClientEntry} entry
   * @param {unknown} collection
   * @returns {{ list: import('@dtronix/nexnet').NexusList<any>, codec: import('./types.js').TypedCodec, name: string }}
   */
  #list(entry, collection) {
    const ref = /** @type {Record<string, unknown>} */ (collection ?? {});
    const name = requireString(ref.name, 'collection.name');
    const description = entry.exp.collection(name);
    if (!description) throw badRequest('The server nexus has no collection ' + name + '.');
    return { list: entry.exp.list(entry.proxy, name), codec: description.item, name };
  }

  /**
   * `invoke` (§6.5): hands the invocation to the runtime before it returns.
   * @param {Record<string, any>} args
   * @param {Context} ctx
   * @returns {unknown}
   */
  #invoke(args, ctx) {
    const entry = this.#invoker(ctx, args.h);
    const methodRef = /** @type {Record<string, unknown>} */ (args.method ?? {});
    const name = requireString(methodRef.name, 'method.name');
    const method = entry.exp.method(name);
    if (!method) throw badRequest('The server nexus has no method ' + name + '.');
    /** @type {any} */
    let pipe = null;
    if (method.pipeKind !== 'None') {
      if (entry instanceof RentalEntry) throw notSupported('Pipes are not supported on a pooled client.');
      if (args.pipe === undefined || args.pipe === null) throw badRequest(name + ' needs a pipe.');
      const pe = this.#pipe(ctx, args.pipe);
      // A pipe of another client of the scope goes to the runtime as is: it refuses a pipe of another session (§6.5).
      pipe = method.pipeKind === 'Channel' ? pe.channelFor(/** @type {import('./types.js').TypedCodec} */ (method.channelItem)) : pe.pipe;
    }
    const cancel = this.#cancel(ctx, entry, args.cancel);
    const proxy = entry.proxy;
    if (cancel.signal?.aborted && method.returnKind !== 'Void') {
      // D17: a call canceled before it is sent fails canceled and is not sent.
      return Promise.reject(cancellationError(cancel.signal));
    }
    entry.pending++;
    /** @type {Promise<unknown>} */
    let call;
    try {
      call = entry.exp.invokeByName(proxy, name, Array.isArray(args.args) ? args.args : [], { pipe, signal: cancel.signal });
    } catch (error) {
      call = Promise.reject(error);
    }
    cancel.start();
    const tracked = call.finally(() => {
      entry.pending--;
      cancel.stop();
    });
    if (args.detach === true) {
      const future = new FutureEntry(this.#newHandle('f'), entry.scope, tracked);
      this.#register(entry.scope, future);
      return { future: future.h };
    }
    return tracked.then((result) => ({ result: result ?? null }));
  }

  /**
   * Forwards a script call to the runner (§7.3).
   * @param {ClientEntry} entry
   * @param {string} slot
   * @param {readonly unknown[]} args
   * @param {AbortSignal} signal
   * @returns {Promise<unknown>}
   */
  #forward(entry, slot, args, signal) {
    const callId = this.#nextCallId++;
    return new Promise((resolve, reject) => {
      const onAbort = () => {
        if (!this.#forwards.delete(callId)) return;
        this.#event(entry, 'script.forwardCancel', { callId });
        reject(cancellationError(signal));
      };
      this.#forwards.set(callId, {
        entry,
        resolve: (value) => {
          signal.removeEventListener('abort', onAbort);
          resolve(value);
        },
        reject: (error) => {
          signal.removeEventListener('abort', onAbort);
          reject(error);
        },
      });
      // Sent even when the signal is already aborted, as the C# and Java hosts do: the runner runs the handler and
      // cancels its token (protocol §7.3).
      this.#event(entry, 'script.forward', { callId, slot, args: [...args] });
      if (signal.aborted) onAbort();
      else signal.addEventListener('abort', onAbort, { once: true });
    });
  }

  /**
   * `pool.create`.
   * @param {Record<string, any>} args
   * @param {Context} ctx
   * @returns {unknown}
   */
  #createPool(args, ctx) {
    const scope = this.#scope(ctx);
    if (!this.#platform.capabilities.includes('ClientPool')) throw notSupported('This client has no pool.');
    const exp = this.#export(args.nexus);
    const options = parsePoolOptions(args.options);
    const parsed = parseSpec(args.spec, { transports: this.#platform.transports, browser: this.#platform.browser });
    if (parsed.onInvocationException) throw notSupported('The client nexus of pooled clients is not scripted: onInvocationException needs a client.');
    const fake = parsed.fakeTime ? new FakeClock() : null;
    const entry = new PoolEntry(this.#newHandle('pl'), scope, exp, fake);
    const host = this;
    // The platform's client class, extended to follow every client the pool creates.
    const Base = /** @type {any} */ (NexusClient);
    class PooledClient extends Base {
      /** @param {...any} rest */
      constructor(...rest) {
        super(...rest);
        entry.clients.add(this);
        entry.created++;
        this.addEventListener('disconnected', (/** @type {Event} */ event) => {
          entry.reasons.set(this, /** @type {CustomEvent} */ (event).detail?.reason ?? 0);
        });
      }
    }
    /** @type {Record<string, any>} */
    const clientConfig = { ...parsed.config, clock: fake ?? systemClock, logger: null };
    if (parsed.logLevel) {
      // Pool logs are not about one client handle; they travel as host log events.
      const min = levelOrder(parsed.logLevel);
      clientConfig.logger = {
        log: (/** @type {string} */ level, /** @type {string} */ message) => {
          if (levelOrder(level) >= min) host.#event(null, 'log', { level: levelName(level), category: entry.h, message });
        },
      };
    }
    const Pool = /** @type {any} */ (NexusClientPool);
    entry.pool = new Pool(
      { clientConfig, maxConnections: options.maxConnections, maxIdleTime: options.maxIdleTime, minIdleConnections: options.minIdleConnections, clientClass: PooledClient },
      () => exp.createNexus(SILENT_SCRIPTS),
      exp.binding,
    );
    this.#register(scope, entry);
    return { pool: entry.h };
  }

  /**
   * `pool.rent`.
   * @param {Record<string, any>} args
   * @param {Context} ctx
   * @returns {Promise<unknown>}
   */
  async #rent(args, ctx) {
    const pool = this.#poolEntry(ctx, args.pool);
    if (pool.disposed) throw invalidOperation('The pool ' + pool.h + ' is disposed.');
    /** @type {AbortSignal | undefined} */
    let signal;
    if (args.cancel !== undefined && args.cancel !== null) {
      const token = requireString(args.cancel.token, 'cancel.token');
      const source = this.#scope(ctx).tokens.get(token);
      if (!source) throw unknownHandle('Unknown cancellation token ' + token + '.');
      signal = source.signal;
    }
    const created = pool.created;
    /** @type {any} */
    let rental;
    try {
      rental = await pool.pool.rent({ signal });
    } catch (error) {
      if (error instanceof NexusClientPoolConnectionError) {
        const result = /** @type {any} */ (error).result;
        /** @type {Record<string, unknown>} */
        const failed = {
          state: resultStateName(result.state),
          reason: reasonName(result.disconnectReason),
          message: error.message,
        };
        if (result.error !== undefined && result.error !== null) failed.error = toWireError(result.error);
        return { failed };
      }
      if (isCancellationError(error) && pool.created > created) {
        // Canceled while the new client connected: a connection result (§6.11).
        return { failed: { state: 'Canceled', reason: 'None', message: 'The connection attempt was canceled.' } };
      }
      if (error instanceof NexusError && !(error instanceof NexusClientPoolConnectionError)) throw invalidOperation(error.message);
      throw error;
    }
    const proxy = rental.proxy;
    let client = null;
    for (const candidate of pool.clients) {
      try {
        if (candidate.proxy === proxy) client = candidate;
      } catch {
        // Not connected: not this one.
      }
    }
    const key = client ?? proxy;
    let id = pool.ids.get(key);
    if (id === undefined) {
      id = 'pc' + this.#nextHandle++;
      pool.ids.set(key, id);
    }
    const entry = new RentalEntry(this.#newHandle('c'), pool.scope, pool, rental, client);
    pool.rentals.set(entry.h, entry);
    this.#register(pool.scope, entry);
    return { h: entry.h, client: id };
  }
}
