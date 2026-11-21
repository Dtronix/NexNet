/**
 * In-memory transport for session tests. The test plays the server: it feeds bytes with {@link LoopbackTransport#serverSend}
 * and reads what the client wrote from {@link LoopbackTransport#sent}.
 * @module
 */

/** A {@link import('../../../src/transport/ITransport.js').Transport} the test drives. */
export class LoopbackTransport {
  /** @type {Uint8Array[]} One entry per `write` call, its chunks concatenated. */
  writes = [];
  closed = false;
  /** @type {unknown} */
  closeReason = undefined;
  isSecure;
  remoteCertificate = null;
  name;
  holdWrites;

  /** @type {((data: Uint8Array) => void) | null} */
  #onData = null;
  /** @type {((error?: unknown) => void) | null} */
  #onClose = null;
  /** @type {Uint8Array[]} */
  #early = [];
  /** @type {Array<{ resolve: () => void, reject: (e: unknown) => void }>} */
  #held = [];

  /** @param {{ isSecure?: boolean, name?: string, holdWrites?: boolean }} [options] */
  constructor({ isSecure = false, name = 'loopback', holdWrites = false } = {}) {
    this.isSecure = isSecure;
    this.name = name;
    this.holdWrites = holdWrites;
  }

  /** @param {Uint8Array[]} chunks @returns {Promise<void>} */
  write(chunks) {
    if (this.closed) return Promise.reject(new Error('transport closed'));
    this.writes.push(concat(chunks));
    if (!this.holdWrites) return Promise.resolve();
    return new Promise((resolve, reject) => this.#held.push({ resolve, reject }));
  }

  /** Completes held writes. */
  releaseWrites() {
    const held = this.#held;
    this.#held = [];
    for (const w of held) w.resolve();
  }

  /** Fails held writes. @param {unknown} error */
  failWrites(error) {
    const held = this.#held;
    this.#held = [];
    for (const w of held) w.reject(error);
  }

  /** @param {(data: Uint8Array) => void} callback */
  onData(callback) {
    this.#onData = callback;
    const early = this.#early;
    this.#early = [];
    for (const chunk of early) callback(chunk);
  }

  /** @param {(error?: unknown) => void} callback */
  onClose(callback) {
    this.#onClose = callback;
  }

  /** @param {unknown} [reason] @returns {Promise<void>} */
  close(reason) {
    this.closed = true;
    this.closeReason = reason;
    return Promise.resolve();
  }

  /** Delivers bytes to the client, as the server. @param {Uint8Array} data */
  serverSend(data) {
    if (this.closed) return;
    if (this.#onData) this.#onData(data);
    else this.#early.push(data);
  }

  /** Ends the stream from the server side. @param {unknown} [error] */
  serverClose(error) {
    if (this.closed) return;
    this.closed = true;
    this.#onClose?.(error);
  }

  /** Everything the client wrote. @returns {Uint8Array} */
  get sent() {
    return concat(this.writes);
  }
}

/** @param {Uint8Array[]} chunks @returns {Uint8Array} */
export function concat(chunks) {
  let n = 0;
  for (const c of chunks) n += c.length;
  const out = new Uint8Array(n);
  let o = 0;
  for (const c of chunks) {
    out.set(c, o);
    o += c.length;
  }
  return out;
}
