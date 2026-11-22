/**
 * The pipe layer of one session (protocol spec §7, C# `NexusPipeManager`): local pipe IDs, receive windows reserved
 * from the session budget and counted against `maxPipes`, both roles of an open (this side opens a pipe with
 * `createPipe` and a pipe method; the server opens one by invoking a client method with a pipe parameter), the
 * `DuplexPipeWrite`, `PipeWindowUpdate` and `DuplexPipeUpdateState` frames, refusals, two-sided close and stalls.
 *
 * The client attaches one manager to every session before it starts ({@link attachPipes}), after the invocation layer,
 * whose dispatch contexts resolve pipe and channel parameters through {@link PipeManager#register}.
 * @module
 */

import { DisconnectReason } from '../contract/enums.js';
import { NexusBusyError, NexusError, NexusTransportError } from '../contract/errors.js';
import { releaseInvocationSlot } from '../invocation/InvocationManager.js';
import { encodeFrame } from '../session/frames.js';
import { MessageType } from '../session/protocol.js';
import { decodeDuplexPipeUpdateState, encodeDuplexPipeUpdateState } from '../session/wire.js';
import { NexusDuplexChannel } from './channels.js';
import { PipeState } from './constants.js';
import { countPipes } from './diagnostics.js';
import { NexusDuplexPipe, pipeCore } from './NexusDuplexPipe.js';
import { PipeCore } from './PipeCore.js';
import { MetricTags } from '../metrics/instruments.js';

const REFUSED_LOCAL = Object.freeze({ [MetricTags.refusalSide]: 'local' });
const RESET_TAGS = Object.freeze({
  invalid_item: Object.freeze({ [MetricTags.resetReason]: 'invalid_item' }),
  too_large: Object.freeze({ [MetricTags.resetReason]: 'too_large' }),
});
/** At most one reset warning per this many milliseconds (C# `NexusSession.ReportPipeReset`). */
const RESET_WARNING_INTERVAL = 10_000;

/** @type {WeakMap<object, PipeManager>} */
const managers = new WeakMap();

/**
 * Attaches a pipe manager to a session that has not started yet, and installs the pipe hooks of its invocation layer.
 * @param {import('../session/NexusSession.js').NexusSession} session
 * @param {import('../invocation/InvocationManager.js').InvocationManager} invocations
 * @returns {PipeManager}
 */
export function attachPipes(session, invocations) {
  const manager = new PipeManager(session, invocations);
  managers.set(session, manager);
  return manager;
}

/**
 * The pipe manager of a session, or null.
 * @param {import('../session/NexusSession.js').NexusSession | null | undefined} session
 * @returns {PipeManager | null}
 */
export function getPipeManager(session) {
  return session ? managers.get(session) ?? null : null;
}

/**
 * Creates a pipe on `session` to pass to a pipe method (`client.createPipe()`).
 * @param {import('../session/NexusSession.js').NexusSession | null} session
 * @returns {NexusDuplexPipe}
 * @throws {NexusTransportError} Not connected.
 * @throws {NexusBusyError} `maxPipes` or the session budget is reached.
 */
export function createPipe(session) {
  const manager = getPipeManager(session);
  if (manager === null || !manager.isOpen) {
    throw new NexusTransportError('Client is not connected.', { reason: session?.disconnectReason ?? 0 });
  }
  return manager.rentPipe();
}

/**
 * Creates a channel on `session` to pass to a channel method (`client.createChannel(formatter)`).
 * @template T
 * @param {import('../session/NexusSession.js').NexusSession | null} session
 * @param {import('../contract/codec.js').Formatter<T>} formatter
 * @returns {NexusDuplexChannel<T>}
 */
export function createChannel(session, formatter) {
  return new NexusDuplexChannel(createPipe(session), formatter);
}

export class PipeManager {
  /** @type {import('../session/NexusSession.js').NexusSession} */
  #session;
  /** @type {import('../session/options.js').ResolvedClientOptions} */
  #options;
  /** @type {import('../invocation/SessionBudget.js').SessionBudget} */
  #budget;
  /** @type {import('./PipeCore.js').PipeHost} */
  #host;
  /** Pipes by full ID, or by partial ID (the local byte) until the peer's Ready. @type {Map<number, PipeCore>} */
  #pipes = new Map();
  /** @type {Set<number>} */
  #usedIds = new Set();
  #lastId = 0;
  /** Pipes holding a receive window (bounded by `maxPipes`). */
  #openPipes = 0;
  #closed = false;
  /** Pipes registered for a dispatched invocation, released when it returns. @type {WeakMap<object, PipeCore[]>} */
  #byContext = new WeakMap();
  /** Resets since the last reset warning. */
  #resetsSinceWarning = 0;
  /** Session clock time of the last reset warning; null before the first. @type {number | null} */
  #lastResetWarning = null;

  /**
   * @param {import('../session/NexusSession.js').NexusSession} session
   * @param {import('../invocation/InvocationManager.js').InvocationManager} invocations
   */
  constructor(session, invocations) {
    this.#session = session;
    this.#options = session.options;
    this.#budget = invocations.budget;
    const options = this.#options;
    this.#host = Object.freeze({
      send: (/** @type {Uint8Array} */ frame) => session.send(frame),
      requestControl: (/** @type {PipeCore} */ core) => session.controlSender.requestPipeControl(core),
      changed: (/** @type {PipeCore} */ core) => this.#tryFree(core),
      now: () => options.clock.now(),
      budget: this.#budget,
      window: options.pipeWindow,
      sendMaxMessageSize: () => session.sendMaxMessageSize,
      maxMessageSize: options.maxMessageSize,
      serializerOptions: options.serializerOptions,
      log: (/** @type {import('../contract/config.js').NexusLogLevel} */ level, /** @type {string} */ message, /** @type {unknown} */ error) => this.#log(level, message, error),
      metric: (/** @type {string} */ name, /** @type {number} */ value, /** @type {Readonly<Record<string, string>> | undefined} */ tags) => session.recordMetric(name, value, tags),
      reset: (/** @type {number} */ pipeId, /** @type {import('./PipeCore.js').PipeResetReason} */ reason) => this.#reportReset(pipeId, reason),
    });

    session.setHandler(MessageType.DuplexPipeWrite, (frame) => this.#onData(frame.pipeId, frame.body));
    session.setHandler(MessageType.PipeWindowUpdate, (frame) => this.#onWindowUpdate(frame.pipeId, frame.body));
    session.setHandler(MessageType.DuplexPipeUpdateState, (frame) => this.#onState(frame.body));
    session.onClose((reason) => this.#cancelAll(reason));
    session.addProgressProbe((now, timeout) => this.#findStalled(now, timeout));
    session.addSnapshotSource({ pipes: (now) => this.snapshots(now) });
    invocations.pipeHooks = {
      pipe: (pipeId, ctx) => this.register(pipeId, ctx),
      channel: (pipeId, formatter, ctx) => new NexusDuplexChannel(this.register(pipeId, ctx), formatter),
      release: (ctx) => this.#releaseContext(ctx),
    };
  }

  /** True while pipes can be opened. @returns {boolean} */
  get isOpen() {
    return !this.#closed && this.#session.isHandshakeComplete && !this.#session.isDisconnecting;
  }

  /** Pipes holding a receive window. @returns {number} */
  get openPipes() {
    return this.#openPipes;
  }

  /**
   * Snapshots of the pipes the session still tracks: open, being opened, or closing until both sides confirmed
   * (C# `NexusPipeManager.AddSnapshots`).
   * @param {number} now Session clock time.
   * @returns {import('../diagnostics/snapshot.js').NexusPipeSnapshot[]}
   */
  snapshots(now) {
    return [...this.#pipes.values()].map((core) => core.snapshot(now));
  }

  // ---------------------------------------------------------------- opening

  /**
   * Opening side: reserves a window and a local ID for a new pipe (C# `RentPipe`).
   * @returns {NexusDuplexPipe}
   */
  rentPipe() {
    if (!this.#reserveWindow()) {
      this.#session.recordMetric('pipeRefusals', 1, REFUSED_LOCAL);
      throw new NexusBusyError("Can't open a pipe: the session's pipe limit or budget is reached.");
    }
    const localId = this.#newLocalId();
    const core = new PipeCore(this, this.#host, localId, 0);
    this.#pipes.set(localId, core);
    return new NexusDuplexPipe(core);
  }

  /**
   * Registering side: the server invoked a client method with a pipe parameter (C# `RegisterPipe`). Reserves a
   * window and a local ID and sends Ready with the window; refuses with `Refused` when a limit is reached. Frees the
   * invocation's execution slot (§8); the pipe is released when the method returns.
   *
   * @param {number} otherId The server's local pipe ID from the arguments.
   * @param {import('../contract/nexus.js').DispatchContext} ctx
   * @returns {NexusDuplexPipe}
   * @throws {NexusBusyError} Refused: `maxPipes` or the session budget is reached.
   */
  register(otherId, ctx) {
    if (!this.isOpen) throw new NexusTransportError("Can't register a pipe: the session is closing.", { reason: this.#session.disconnectReason });
    if (!Number.isInteger(otherId) || otherId < 1 || otherId > 255) throw new NexusError(`Invalid pipe ID ${otherId} in the arguments.`);
    if (!this.#reserveWindow()) {
      this.#refuse(otherId);
      throw new NexusBusyError('Refused a pipe: the session\'s pipe limit or budget is reached.');
    }
    const localId = this.#newLocalId();
    const fullId = localId | (otherId << 8);
    const core = new PipeCore(this, this.#host, localId, fullId);
    this.#pipes.set(fullId, core);
    const pipe = new NexusDuplexPipe(core);
    void core.sendReady();

    const registered = this.#byContext.get(ctx);
    if (registered) registered.push(core);
    else this.#byContext.set(ctx, [core]);
    // The pipe now counts against the pipe limit; free the execution slot for other invocations.
    releaseInvocationSlot(ctx);
    return pipe;
  }

  /**
   * Opening side: marks a pipe as sent with a pipe method. Throws when it can't be.
   * @param {NexusDuplexPipe} pipe
   * @returns {PipeCore}
   */
  beginInvoke(pipe) {
    const core = pipeCore(pipe);
    if (core.manager !== this) {
      throw new NexusError('The pipe belongs to another session; create pipes on the current connection.');
    }
    if (!core.initiating) {
      throw new NexusError('The pipe was opened by the server; pass a pipe from createPipe(). Pipes can only be used once.');
    }
    if (core.released) throw new NexusError('The pipe was disposed.');
    if (core.phase !== 'created') throw new NexusError('The pipe is already open on another invocation. Pipes can only be used once.');
    core.phase = 'invoked';
    return core;
  }

  /**
   * Opening side: the pipe method was not sent, so the pipe fails and is freed.
   * @param {PipeCore} core
   * @param {unknown} error
   */
  invokeFailed(core, error) {
    core.invokeFailed(error);
    core.released = true;
    this.#tryFree(core);
  }

  /**
   * The application is done with a pipe (dispose, or the registered method returned): sends this side's Complete if
   * needed and frees the pipe once the peer has closed it too (C# `ReleasePipe`). A pipe the peer never saw is freed
   * at once; one whose open is in flight closes as soon as Ready arrives.
   * @param {PipeCore} core
   * @returns {Promise<void>}
   */
  async release(core) {
    if (core.released || core.freed) return;
    core.released = true;
    /** @type {Promise<void>} */
    let closing = Promise.resolve();
    if (core.phase === 'created' || core.phase === 'failed') core.markClosedWithoutExchange();
    else if (core.phase === 'open') closing = core.close();
    core.dispose();
    this.#tryFree(core);
    await closing;
  }

  /** @param {object} ctx */
  #releaseContext(ctx) {
    const registered = this.#byContext.get(ctx);
    if (!registered) return;
    this.#byContext.delete(ctx);
    for (const core of registered) void this.release(core);
  }

  /** Takes a pipe slot and reserves one receive window from the session budget, all or nothing. */
  #reserveWindow() {
    if (this.#openPipes + 1 > this.#options.maxPipes) return false;
    if (!this.#budget.tryReserve(this.#options.pipeWindow)) return false;
    this.#openPipes++;
    countPipes('openPipes', 1);
    return true;
  }

  /** Round-robin local ID, 1 to 255 (§7.5). Never fails while `maxPipes` ≤ 255 bounds the IDs in use. */
  #newLocalId() {
    for (let i = 0; i < 255; i++) {
      this.#lastId = this.#lastId === 255 ? 1 : this.#lastId + 1;
      if (!this.#usedIds.has(this.#lastId)) {
        this.#usedIds.add(this.#lastId);
        return this.#lastId;
      }
    }
    throw new NexusError('Exceeded the maximum number of concurrent pipes.');
  }

  /**
   * Refuses a pipe the server opened (§7.1): `Refused` with the opener's partial ID.
   * @param {number} otherId
   */
  #refuse(otherId) {
    this.#session.recordMetric('pipeRefusals', 1, REFUSED_LOCAL);
    const partial = (otherId << 8) & 0xffff;
    void this.#session.send(encodeFrame(MessageType.DuplexPipeUpdateState, encodeDuplexPipeUpdateState(partial, PipeState.Refused, 0)));
  }

  /**
   * Frees a pipe once both sides closed it and the application released it: removes it, releases its window and
   * returns its ID.
   * @param {PipeCore} core
   */
  #tryFree(core) {
    if (core.freed || !core.isFullyClosed) return;
    core.markFreed();
    const key = core.id !== 0 ? core.id : core.localId;
    if (this.#pipes.get(key) === core) this.#pipes.delete(key);
    this.#usedIds.delete(core.localId);
    this.#openPipes--;
    countPipes('openPipes', -1);
    this.#budget.release(this.#options.pipeWindow + core.extraOutstanding);
    core.extraOutstanding = 0;
    core.dropInput();
  }

  /**
   * Waits until a pipe is freed: both sides closed it and the application released it, so its slot, window and ID
   * are available again (C# `NexusPipeManager.WaitFreedAsync`). Also resolves when the session ends, and after
   * `progressTimeout` when the server never confirms the close (the session's stall detection reports that). Never
   * rejects.
   * @param {PipeCore} core
   * @returns {Promise<void>}
   */
  whenFreed(core) {
    if (core.freed || core.manager !== this) return Promise.resolve();
    const clock = this.#options.clock;
    return new Promise((resolve) => {
      const timer = clock.setTimeout(() => {
        this.#log('debug', `Pipe P${core.id} was not freed within the progress timeout.`);
        resolve();
      }, this.#options.progressTimeout);
      void core.whenFreed.then(() => {
        clock.clearTimeout(timer);
        resolve();
      });
    });
  }

  /**
   * A channel item reset a pipe (C# `NexusSession.ReportPipeReset`): records `nexnet.pipe.resets` and logs a
   * warning at most once per 10 s, with the number of resets since the last one, so a server that keeps sending
   * broken items does not flood the log.
   * @param {number} pipeId
   * @param {import('./PipeCore.js').PipeResetReason} reason
   */
  #reportReset(pipeId, reason) {
    this.#session.recordMetric('pipeResets', 1, RESET_TAGS[reason]);
    this.#resetsSinceWarning++;
    const now = this.#options.clock.now();
    const last = this.#lastResetWarning;
    if (last !== null && now - last < RESET_WARNING_INTERVAL) return;
    this.#lastResetWarning = now;
    const resets = this.#resetsSinceWarning;
    this.#resetsSinceWarning = 0;
    const cause = reason === 'too_large' ? 'a channel item exceeds MaxMessageSize' : 'a channel item breaks the framing';
    this.#log('warning', `Reset pipe P${String(pipeId).padStart(5, '0')}: ${cause}`
      + (resets > 1 ? ` (${resets} pipe resets since the last warning).` : '.'));
  }

  // ---------------------------------------------------------------- frames

  /**
   * @param {number} pipeId
   * @param {Uint8Array} body
   * @returns {number}
   */
  #onData(pipeId, body) {
    if (this.#closed) return 0;
    // Pipes stay here while closing, so data the peer sent before learning of a local close is accepted (and
    // discarded) up to its credit.
    const core = this.#pipes.get(pipeId);
    if (!core || core.id === 0) {
      this.#log('debug', `Received data for pipe P${pipeId}, which is neither open nor closing.`);
      return DisconnectReason.ProtocolError;
    }
    if (!core.onData(body)) {
      this.#log('debug', `Pipe P${pipeId} received data beyond its credit or after the peer's Complete.`);
      return DisconnectReason.FlowControl;
    }
    return 0;
  }

  /**
   * @param {number} pipeId
   * @param {Uint8Array} body
   * @returns {number}
   */
  #onWindowUpdate(pipeId, body) {
    if (body.length !== 4) {
      this.#log('warning', `PipeWindowUpdate for P${pipeId} has a ${body.length}-byte body.`);
      return DisconnectReason.ProtocolError;
    }
    // A grant can race with this side freeing the pipe; it no longer matters.
    const core = this.#pipes.get(pipeId);
    if (this.#closed || !core || core.id === 0) return 0;
    const credit = new DataView(body.buffer, body.byteOffset, 4).getUint32(0, true);
    if (!core.addSendCredit(credit)) {
      this.#log('debug', `Pipe P${pipeId} was granted more than 2^31 bytes of credit.`);
      return DisconnectReason.FlowControl;
    }
    return 0;
  }

  /**
   * @param {Uint8Array} body
   * @returns {number}
   */
  #onState(body) {
    /** @type {{ pipeId: number, state: number, window: number }} */
    let message;
    try {
      message = decodeDuplexPipeUpdateState(body);
    } catch (error) {
      this.#log('warning', 'DuplexPipeUpdateState did not decode.', error);
      return DisconnectReason.ProtocolError;
    }
    if (this.#closed) return 0;
    const { pipeId, state, window } = message;

    // A refusal carries the opener's partial ID, so it is found here too.
    const core = this.#pipes.get(pipeId);
    if (core) {
      if (core.id === 0 && (state & PipeState.Refused) === 0) {
        this.#log('warning', `DuplexPipeUpdateState ${state} for pipe P${pipeId}, which has no server ID yet.`);
        return DisconnectReason.ProtocolError;
      }
      const reason = core.onRemoteState(state, window);
      // Refused: nothing to exchange and nothing to use; free it now.
      if ((state & PipeState.Refused) !== 0 && core.phase === 'failed') core.released = true;
      this.#tryFree(core);
      return reason;
    }

    if (state === PipeState.Ready) {
      const partial = pipeId & 0xff;
      const pending = this.#pipes.get(partial);
      if (!pending || pending.id !== 0) {
        this.#log('warning', `Ready for pipe P${pipeId} matches no pending pipe.`);
        return DisconnectReason.ProtocolError;
      }
      this.#pipes.delete(partial);
      this.#pipes.set(pipeId, pending);
      pending.id = pipeId;
      const reason = pending.onRemoteState(state, window);
      this.#tryFree(pending);
      return reason;
    }
    this.#log('trace', `Ignored state ${state} for pipe P${pipeId}.`);
    return 0;
  }

  /**
   * The session ended: every pipe completes or fails, and nothing is freed into the closed budget.
   * @param {number} reason
   */
  #cancelAll(reason) {
    this.#closed = true;
    for (const core of this.#pipes.values()) core.sessionEnded(reason);
    countPipes('openPipes', -this.#openPipes);
    this.#openPipes = 0;
    this.#pipes.clear();
    this.#usedIds.clear();
  }

  /**
   * @param {number} now
   * @param {number} timeout
   * @returns {string | null}
   */
  #findStalled(now, timeout) {
    for (const core of this.#pipes.values()) {
      const stall = core.describeStall(now, timeout);
      if (stall !== null) return stall;
    }
    return null;
  }

  /**
   * @param {import('../contract/config.js').NexusLogLevel} level
   * @param {string} message
   * @param {unknown} [error]
   */
  #log(level, message, error) {
    const logger = this.#options.logger;
    if (!logger) return;
    try {
      const id = this.#session.id;
      logger.log(level, id === null ? message : `S${id}: ${message}`, error);
    } catch {
      // A logger must never break the session.
    }
  }
}
