import { describe, expect, it } from 'vitest';
import { MessageType } from '../../src/session/protocol.js';
import {
  BodyReader, BodyWriter, WireFormatError,
  decodeClientGreeting, decodeDuplexPipeUpdateState, decodeInvocationCancellation, decodeInvocationCredit,
  decodeMessageAbort, decodeServerGreeting, encodeClientGreeting, encodeDuplexPipeUpdateState,
  encodeInvocationCancellation, encodeInvocationCredit, encodeMessageAbort, encodeServerGreeting,
} from '../../src/session/wire.js';
import { fromHex, messageVectors, toHex } from './support/fixtures.js';

/** @param {number} type @param {Record<string, any>} f @returns {Uint8Array} */
function encode(type, f) {
  switch (type) {
    case MessageType.ClientGreeting:
    case MessageType.ClientGreetingReconnection:
      return encodeClientGreeting(/** @type {any} */ ({ ...f, authenticationToken: fromHex(f.authenticationToken) }));
    case MessageType.ServerGreeting:
      return encodeServerGreeting(/** @type {any} */ ({ ...f, clientId: BigInt(f.clientId) }));
    case MessageType.InvocationCredit: return encodeInvocationCredit(f.credits);
    case MessageType.InvocationCancellation: return encodeInvocationCancellation(f.invocationId);
    case MessageType.MessageAbort: return encodeMessageAbort(f.invocationId, f.kind, f.reason);
    case MessageType.DuplexPipeUpdateState: return encodeDuplexPipeUpdateState(f.pipeId, f.state, f.window);
    default: throw new Error(`no encoder for ${type}`);
  }
}

/** @param {number} type @param {Uint8Array} body @returns {Record<string, any>} */
function decode(type, body) {
  switch (type) {
    case MessageType.ClientGreeting:
    case MessageType.ClientGreetingReconnection: {
      const g = decodeClientGreeting(body);
      return { ...g, authenticationToken: toHex(/** @type {Uint8Array} */ (g.authenticationToken)) };
    }
    case MessageType.ServerGreeting: {
      const { idleTimeoutMs, ...g } = decodeServerGreeting(body);
      expect(idleTimeoutMs).toBeNull();
      return { ...g, clientId: g.clientId.toString() };
    }
    case MessageType.InvocationCredit: return decodeInvocationCredit(body);
    case MessageType.InvocationCancellation: return decodeInvocationCancellation(body);
    case MessageType.MessageAbort: return decodeMessageAbort(body);
    case MessageType.DuplexPipeUpdateState: return decodeDuplexPipeUpdateState(body);
    default: throw new Error(`no decoder for ${type}`);
  }
}

describe('session message bodies match the C# vectors (fixtures/messages.jsonl)', () => {
  it('has vectors for every session message', () => {
    const types = new Set(messageVectors.map((v) => v.type));
    for (const t of [100, 101, 105, 111, 115, 116, 120]) expect(types.has(t), String(t)).toBe(true);
  });

  for (const v of messageVectors) {
    it(`encodes ${v.name}`, () => {
      expect(toHex(encode(v.type, v.fields))).toBe(v.body);
    });
    it(`decodes ${v.name}`, () => {
      expect(decode(v.type, fromHex(v.body))).toEqual(v.fields);
    });
  }
});

describe('ServerGreeting trailing fields (S04)', () => {
  it('reads idleTimeoutMs as the fifth element', () => {
    const body = encodeServerGreeting({ version: 1, clientId: 2n, maxMessageSize: 65526, invocationCredits: 1, idleTimeoutMs: 30000 });
    expect(decodeServerGreeting(body).idleTimeoutMs).toBe(30000);
  });

  it('reads idleTimeoutMs as uint32: values above int32 are accepted, negatives rejected (J15)', () => {
    const big = encodeServerGreeting({ version: 1, clientId: 2n, maxMessageSize: 65526, invocationCredits: 1, idleTimeoutMs: 0xffffffff });
    expect(decodeServerGreeting(big).idleTimeoutMs).toBe(0xffffffff);
    const neg = new BodyWriter().writeArrayHeader(5).writeInt(1).writeInt(2).writeInt(65526).writeInt(1).writeInt(-1).toUint8Array();
    expect(() => decodeServerGreeting(neg)).toThrow(WireFormatError);
  });

  it('accepts nil idleTimeoutMs and skips further trailing elements', () => {
    const w = new BodyWriter().writeArrayHeader(7).writeInt(1).writeInt(2).writeInt(65526).writeInt(1).writeNil();
    w.writeString('future').writeArrayHeader(2).writeInt(1).writeInt(-1);
    const g = decodeServerGreeting(w.toUint8Array());
    expect(g.idleTimeoutMs).toBeNull();
    expect(g.clientId).toBe(2n);
  });

  it('rejects fewer than four elements, trailing bytes and out-of-range values', () => {
    expect(() => decodeServerGreeting(new BodyWriter().writeArrayHeader(3).writeInt(1).writeInt(2).writeInt(3).toUint8Array()))
      .toThrow(WireFormatError);
    const ok = encodeServerGreeting({ version: 1, clientId: 2n, maxMessageSize: 65526, invocationCredits: 1 });
    expect(() => decodeServerGreeting(Uint8Array.of(...ok, 0xc0))).toThrow(/trailing/);
    const big = new BodyWriter().writeArrayHeader(4).writeInt(0x80000000).writeInt(0).writeInt(0).writeInt(0).toUint8Array();
    expect(() => decodeServerGreeting(big)).toThrow(/out of range/);
    expect(() => decodeServerGreeting(Uint8Array.of(0x94, 0x01))).toThrow(/end of body/);
  });
});

describe('canonical integers', () => {
  const cases = /** @type {Array<[number | bigint, string]>} */ ([
    [0, '00'], [127, '7f'], [128, 'cc80'], [255, 'ccff'], [256, 'cd0100'], [65535, 'cdffff'], [65536, 'ce00010000'],
    [0xffffffff, 'ceffffffff'], [0x100000000, 'cf0000000100000000'], [-1, 'ff'], [-32, 'e0'], [-33, 'd0df'],
    [-128, 'd080'], [-129, 'd1ff7f'], [-32768, 'd18000'], [-32769, 'd2ffff7fff'], [-0x80000000, 'd280000000'],
    [-0x80000001, 'd3ffffffff7fffffff'], [0x7fffffffffffffffn, 'cf7fffffffffffffff'], [-0x8000000000000000n, 'd38000000000000000'],
    [0xffffffffffffffffn, 'cfffffffffffffffff'], [5n, '05'],
  ]);
  for (const [value, hex] of cases) {
    it(`${value} -> ${hex}`, () => {
      expect(toHex(new BodyWriter().writeInt(value).toUint8Array())).toBe(hex);
      expect(new BodyReader(fromHex(hex)).readBigInt()).toBe(BigInt(value));
    });
  }

  it('rejects unsafe numbers', () => {
    expect(() => new BodyWriter().writeInt(2 ** 53)).toThrow(RangeError);
    expect(() => new BodyWriter().writeInt(1.5)).toThrow(RangeError);
  });

  it('strings and bin use the smallest header', () => {
    expect(toHex(new BodyWriter().writeString('a'.repeat(31)).toUint8Array()).slice(0, 2)).toBe('bf');
    expect(toHex(new BodyWriter().writeString('a'.repeat(32)).toUint8Array()).slice(0, 4)).toBe('d920');
    expect(toHex(new BodyWriter().writeBin(new Uint8Array(256)).toUint8Array()).slice(0, 6)).toBe('c50100');
    const invalidUtf8 = new BodyReader(Uint8Array.of(0xa2, 0xc3, 0x28));
    expect(() => invalidUtf8.readString()).toThrow(/UTF-8/);
  });

  it('enforces the bin length limit before reading the bytes', () => {
    const r = new BodyReader(Uint8Array.of(0xc5, 0x20, 0x01));
    expect(() => r.readBin(8192)).toThrow(/exceeds/);
  });
});
