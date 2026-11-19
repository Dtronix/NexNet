/**
 * Base64, SHA-256 (incremental) and the pipe pattern; platform-neutral (Node and browsers).
 * @module
 */

/**
 * @param {Uint8Array} bytes
 * @returns {string}
 */
export function toBase64(bytes) {
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

/**
 * @param {string} text
 * @returns {Uint8Array}
 */
export function fromBase64(text) {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/**
 * Bytes of the deterministic pipe pattern (C# `PipePattern`): byte `i` is `(i * 31 + seed) mod 256`.
 * @param {number} offset Index of the first byte.
 * @param {number} length
 * @param {number} seed
 * @returns {Uint8Array}
 */
export function pattern(offset, length, seed) {
  const bytes = new Uint8Array(length);
  // (i * 31 + seed) mod 256 only depends on i mod 256.
  const base = offset % 256;
  for (let j = 0; j < length; j++) bytes[j] = (((base + j) % 256) * 31 + seed) & 0xff;
  return bytes;
}

const K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01,
  0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
  0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
  0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08,
  0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
  0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

/** Incremental SHA-256 (FIPS 180-4). */
export class Sha256 {
  #h = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
  #block = new Uint8Array(64);
  #blockLength = 0;
  #total = 0;
  #w = new Uint32Array(64);

  /**
   * @param {Uint8Array} data
   * @returns {this}
   */
  update(data) {
    let i = 0;
    this.#total += data.length;
    if (this.#blockLength > 0) {
      const take = Math.min(64 - this.#blockLength, data.length);
      this.#block.set(data.subarray(0, take), this.#blockLength);
      this.#blockLength += take;
      i = take;
      if (this.#blockLength === 64) {
        this.#compress(this.#block, 0);
        this.#blockLength = 0;
      }
    }
    for (; i + 64 <= data.length; i += 64) this.#compress(data, i);
    if (i < data.length) {
      this.#block.set(data.subarray(i), 0);
      this.#blockLength = data.length - i;
    }
    return this;
  }

  /** The digest as lower-case hex. Ends the hash. @returns {string} */
  hex() {
    const bits = this.#total * 8;
    const tail = new Uint8Array((this.#blockLength < 56 ? 64 : 128) - this.#blockLength);
    tail[0] = 0x80;
    const view = new DataView(tail.buffer);
    view.setUint32(tail.length - 8, Math.floor(bits / 0x100000000), false);
    view.setUint32(tail.length - 4, bits >>> 0, false);
    this.#total -= tail.length; // padding does not count
    this.update(tail);
    let out = '';
    for (const word of this.#h) out += word.toString(16).padStart(8, '0');
    return out;
  }

  /**
   * @param {Uint8Array} data
   * @param {number} offset
   */
  #compress(data, offset) {
    const w = this.#w;
    for (let t = 0; t < 16; t++) {
      const j = offset + t * 4;
      w[t] = ((data[j] << 24) | (data[j + 1] << 16) | (data[j + 2] << 8) | data[j + 3]) >>> 0;
    }
    for (let t = 16; t < 64; t++) {
      const x = w[t - 15];
      const y = w[t - 2];
      const s0 = ((x >>> 7) | (x << 25)) ^ ((x >>> 18) | (x << 14)) ^ (x >>> 3);
      const s1 = ((y >>> 17) | (y << 15)) ^ ((y >>> 19) | (y << 13)) ^ (y >>> 10);
      w[t] = (w[t - 16] + s0 + w[t - 7] + s1) >>> 0;
    }
    const h = this.#h;
    let a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], k = h[7];
    for (let t = 0; t < 64; t++) {
      const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
      const ch = (e & f) ^ (~e & g);
      const t1 = (k + S1 + ch + K[t] + w[t]) >>> 0;
      const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
      const maj = (a & b) ^ (a & c) ^ (b & c);
      const t2 = (S0 + maj) >>> 0;
      k = g;
      g = f;
      f = e;
      e = (d + t1) >>> 0;
      d = c;
      c = b;
      b = a;
      a = (t1 + t2) >>> 0;
    }
    h[0] = (h[0] + a) >>> 0;
    h[1] = (h[1] + b) >>> 0;
    h[2] = (h[2] + c) >>> 0;
    h[3] = (h[3] + d) >>> 0;
    h[4] = (h[4] + e) >>> 0;
    h[5] = (h[5] + f) >>> 0;
    h[6] = (h[6] + g) >>> 0;
    h[7] = (h[7] + k) >>> 0;
  }
}
