/**
 * Replays the golden vectors (`tests/golden`, format v1: `tests/golden/README.md`) against the JS codec. Platform
 * neutral: the Node suite (`test/golden.test.js`) and the browser suite (`golden.browser.test.js`) pass the file
 * texts in.
 *
 * Each vector `type` maps to a codec: a built-in formatter, a combinator of formatters, or, for the declared types of
 * `schemas.jsonl`, a formatter built from the schema here (the shape J03 generates). Values convert between typed
 * JSON and the runtime's JS values, so every vector runs through the real formatters both ways.
 * @module
 */

import { expect } from 'vitest';
import {
  MsgPackReader, MsgPackWriter, NexusSerializationError, halfFromBits, halfToBits,
  arrayFormatter, bigIntegerFormatter, binFormatter, boolFormatter, byteFormatter, charArrayFormatter, charFormatter,
  dateOnlyFormatter, dateTimeFormatter, dateTimeOffsetFormatter, decimalFormatter, float32ArrayFormatter,
  float32Formatter, float64ArrayFormatter, float64Formatter, guidFormatter, halfArrayFormatter, halfFormatter,
  int16ArrayFormatter, int16Formatter, int32ArrayFormatter, int32Formatter, int64ArrayFormatter, int64Formatter,
  int8ArrayFormatter, mapFormatter, nullableFormatter, rawFormatter, sbyteFormatter, setFormatter, stringFormatter,
  timeOnlyFormatter, timeSpanFormatter, tupleFormatter, uint16ArrayFormatter, uint16Formatter, uint32ArrayFormatter,
  uint32Formatter, uint64ArrayFormatter, uint64Formatter, uriFormatter, versionFormatter,
} from '../../../src/serialization/index.js';
import {
  NexusDateOnly, NexusDateTime, NexusDateTimeOffset, NexusDecimal, NexusTimeOnly, NexusTimeSpan, NexusVersion,
} from '../../../src/values/index.js';
import { listMessageFormatter } from '../../../src/collections/messages.js';

/** The vector files every runtime replays (README "Files"); any other `*.jsonl` next to them is replayed too. */
export const VECTOR_FILES = Object.freeze(['primitives', 'containers', 'ext78', 'objects', 'unions', 'messages', 'frames', 'reject']);

/**
 * The vector files to replay: {@link VECTOR_FILES} first, then any other `*.jsonl` present (`schemas.jsonl` excluded),
 * so a new vector file is replayed as soon as it lands.
 * @param {string[]} present File names without the `.jsonl` extension.
 * @returns {string[]}
 */
export function vectorFiles(present) {
  const extra = present.filter((f) => f !== 'schemas' && !VECTOR_FILES.includes(f)).sort();
  return [...VECTOR_FILES, ...extra];
}

/**
 * Declared objects that are C# structs, so never nil. `schemas.jsonl` does not record it; these come from
 * `src/NexNet.TestNexus/Interop/InteropTypes.cs`.
 */
const STRUCT_OBJECTS = new Set(['InteropStruct', 'InteropRecordStruct']);

const VECTOR_FIELDS = new Set(['id', 'type', 'mode', 'options', 'error', 'note', 'value', 'hex']);

/**
 * @typedef {object} Vector
 * @property {string} file
 * @property {string} id
 * @property {string} type
 * @property {'both' | 'decode' | 'reject'} mode
 * @property {{ security?: 'trusted' | 'untrusted', maxDepth?: number, strictUtf8?: boolean, maxElements?: number }} [options]
 * @property {string} [error]
 * @property {any} [value]
 * @property {string} hex
 */

/**
 * @typedef {object} Codec
 * @property {import('../../../src/serialization/index.js').Formatter<any>} f
 * @property {(json: any) => any} toJs Typed JSON to the runtime's JS value.
 * @property {(value: any) => any} toJson The runtime's JS value to typed JSON.
 * @property {any} [zero] Typed JSON of C# `default(T)` for a value type (a member missing from a shorter object
 *   array decodes as it); undefined for a reference type (null).
 */

/** @param {string} text @returns {string[]} */
function lines(text) {
  return text.split(/\r?\n/).filter((l) => l.trim() !== '');
}

/**
 * Parses one vector file. Refuses a line with a field the format does not define.
 * @param {string} file
 * @param {string} text
 * @returns {Vector[]}
 */
export function parseVectors(file, text) {
  return lines(text).map((line) => {
    const v = JSON.parse(line);
    for (const key of Object.keys(v)) {
      if (!VECTOR_FIELDS.has(key)) throw new Error(`${file}: vector ${v.id} has the unknown field '${key}'`);
    }
    return { ...v, file };
  });
}

// ------------------------------------------------------------------ Hex, base64, typed-JSON scalars

/** @param {string} hex @returns {Uint8Array} */
export function fromHex(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  return out;
}

/** @param {Uint8Array} bytes @returns {string} */
export function toHex(bytes) {
  let s = '';
  for (const b of bytes) s += (b < 16 ? '0' : '') + b.toString(16);
  return s;
}

/** @param {string} text @returns {Uint8Array} */
function fromBase64(text) {
  const bin = atob(text);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** @param {Uint8Array} bytes @returns {string} */
function toBase64(bytes) {
  let bin = '';
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(bin);
}

/** @param {bigint | number} value @returns {number | string} */
function intJson(value) {
  if (typeof value === 'number') return value;
  return value >= BigInt(Number.MIN_SAFE_INTEGER) && value <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(value) : value.toString();
}

const scratch = new DataView(new ArrayBuffer(8));

/** @param {string} text @param {string} prefix @param {number} digits @returns {bigint} */
function floatBits(text, prefix, digits) {
  if (!text.startsWith(prefix) || text.length !== prefix.length + digits) throw new Error(`not a ${prefix} bit pattern: ${text}`);
  return BigInt(`0x${text.slice(prefix.length)}`);
}

/** @param {bigint} bits @param {string} prefix @param {number} digits @returns {string} */
function bitsJson(bits, prefix, digits) {
  return prefix + bits.toString(16).toUpperCase().padStart(digits, '0');
}

/** @param {any} j @returns {number} */
function f32FromJson(j) {
  if (typeof j === 'number') return Math.fround(j);
  scratch.setUint32(0, Number(floatBits(j, 'f32:0x', 8)));
  return scratch.getFloat32(0);
}

/** @param {number} v @returns {string} */
function f32Json(v) {
  scratch.setFloat32(0, v);
  return bitsJson(BigInt(scratch.getUint32(0)), 'f32:0x', 8);
}

/** @param {any} j @returns {number} */
function f64FromJson(j) {
  if (typeof j === 'number') return j;
  scratch.setBigUint64(0, floatBits(j, 'f64:0x', 16));
  return scratch.getFloat64(0);
}

/** @param {number} v @returns {string} */
function f64Json(v) {
  scratch.setFloat64(0, v);
  return bitsJson(scratch.getBigUint64(0), 'f64:0x', 16);
}

/** @param {any} j @returns {number} */
function f16FromJson(j) {
  return typeof j === 'number' ? j : halfFromBits(Number(floatBits(j, 'f16:0x', 4)));
}

/** @param {number} v @returns {string} */
function f16Json(v) {
  return bitsJson(BigInt(halfToBits(v)), 'f16:0x', 4);
}

/**
 * True for a typed-JSON NaN bit pattern.
 * @param {unknown} j
 * @returns {boolean}
 */
function isNaNJson(j) {
  if (typeof j !== 'string') return false;
  if (j.startsWith('f32:0x')) return Number.isNaN(f32FromJson(j));
  if (j.startsWith('f64:0x')) return Number.isNaN(f64FromJson(j));
  if (j.startsWith('f16:0x')) return Number.isNaN(f16FromJson(j));
  return false;
}

/** @param {unknown} j @returns {boolean} */
function containsNaN(j) {
  return Array.isArray(j) ? j.some(containsNaN) : isNaNJson(j);
}

/**
 * Typed-JSON equality where any NaN equals any NaN (README "NaN": a JS number cannot hold a NaN's sign and payload).
 * @param {unknown} a
 * @param {unknown} b
 * @returns {boolean}
 */
export function jsonEquals(a, b) {
  if (Array.isArray(a) || Array.isArray(b)) {
    return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((x, i) => jsonEquals(x, b[i]));
  }
  if (a === b) return true;
  return isNaNJson(a) && isNaNJson(b) && String(a).slice(0, 4) === String(b).slice(0, 4);
}

// ------------------------------------------------------------------ Type expressions

/**
 * @typedef {{ kind: 'name', name: string, args: TypeNode[] } | { kind: 'tuple', items: TypeNode[] } | { kind: 'array', element: TypeNode } | { kind: 'nullable', inner: TypeNode }} TypeNode
 */

/**
 * Parses a type expression (README "Types").
 * @param {string} text
 * @returns {TypeNode}
 */
export function parseType(text) {
  let i = 0;
  /** @returns {TypeNode} */
  function type() {
    /** @type {TypeNode} */
    let node;
    if (text[i] === '(') {
      i++;
      const items = [type()];
      while (text[i] === ',') {
        i++;
        items.push(type());
      }
      expectChar(')');
      node = { kind: 'tuple', items };
    } else {
      const m = /^[A-Za-z_$][A-Za-z0-9_]*/.exec(text.slice(i));
      if (!m) throw new Error(`bad type expression '${text}' at ${i}`);
      i += m[0].length;
      /** @type {TypeNode[]} */
      const args = [];
      if (text[i] === '<') {
        i++;
        args.push(type());
        while (text[i] === ',') {
          i++;
          args.push(type());
        }
        expectChar('>');
      }
      node = { kind: 'name', name: m[0], args };
    }
    for (;;) {
      if (text.startsWith('[]', i)) {
        i += 2;
        node = { kind: 'array', element: node };
      } else if (text[i] === '?') {
        i++;
        node = { kind: 'nullable', inner: node };
      } else {
        return node;
      }
    }
  }
  /** @param {string} c */
  function expectChar(c) {
    if (text[i] !== c) throw new Error(`bad type expression '${text}': expected '${c}' at ${i}`);
    i++;
  }
  const node = type();
  if (i !== text.length) throw new Error(`bad type expression '${text}': trailing text at ${i}`);
  return node;
}

/** @param {TypeNode} node @returns {string} */
function typeKey(node) {
  switch (node.kind) {
    case 'name': return node.args.length ? `${node.name}<${node.args.map(typeKey).join(',')}>` : node.name;
    case 'tuple': return `(${node.items.map(typeKey).join(',')})`;
    case 'array': return `${typeKey(node.element)}[]`;
    default: return `${typeKey(node.inner)}?`;
  }
}

/** @param {TypeNode} node @param {TypeNode[]} bindings @returns {TypeNode} */
function bind(node, bindings) {
  switch (node.kind) {
    case 'name': {
      const m = /^\$(\d+)$/.exec(node.name);
      if (m) {
        const bound = bindings[Number(m[1])];
        if (!bound) throw new Error(`unbound type parameter ${node.name}`);
        return bound;
      }
      return { kind: 'name', name: node.name, args: node.args.map((a) => bind(a, bindings)) };
    }
    case 'tuple': return { kind: 'tuple', items: node.items.map((a) => bind(a, bindings)) };
    case 'array': return { kind: 'array', element: bind(node.element, bindings) };
    default: return { kind: 'nullable', inner: bind(node.inner, bindings) };
  }
}

// ------------------------------------------------------------------ Codecs

/** @param {(json: any) => any} toJs @param {(value: any) => any} toJson @returns {[(json: any) => any, (value: any) => any]} */
function nullSafe(toJs, toJson) {
  return [(j) => (j === null ? null : toJs(j)), (v) => (v === null ? null : toJson(v))];
}

/**
 * @param {import('../../../src/serialization/index.js').Formatter<any>} f
 * @param {(json: any) => any} [toJs]
 * @param {(value: any) => any} [toJson]
 * @param {any} [zero] Typed JSON of `default(T)` for a value type.
 * @returns {Codec}
 */
function codec(f, toJs = (j) => j, toJson = (v) => v, zero = undefined) {
  const [a, b] = nullSafe(toJs, toJson);
  return { f, toJs: a, toJson: b, zero };
}

const BIN = codec(binFormatter, fromBase64, toBase64);

/** @type {Record<string, Codec>} */
const PRIMS = {
  bool: codec(boolFormatter, undefined, undefined, false),
  sbyte: codec(sbyteFormatter, Number, undefined, 0),
  byte: codec(byteFormatter, Number, undefined, 0),
  short: codec(int16Formatter, Number, undefined, 0),
  ushort: codec(uint16Formatter, Number, undefined, 0),
  int: codec(int32Formatter, Number, undefined, 0),
  uint: codec(uint32Formatter, Number, undefined, 0),
  long: codec(int64Formatter, BigInt, intJson, 0),
  ulong: codec(uint64Formatter, BigInt, intJson, 0),
  char: codec(charFormatter, (j) => String.fromCharCode(Number(j)), (v) => v.charCodeAt(0), 0),
  float: codec(float32Formatter, f32FromJson, f32Json, 'f32:0x00000000'),
  double: codec(float64Formatter, f64FromJson, f64Json, 'f64:0x0000000000000000'),
  half: codec(halfFormatter, f16FromJson, f16Json, 'f16:0x0000'),
  decimal: codec(decimalFormatter, (j) => NexusDecimal.fromBits(j[0], j[1], j[2], j[3]), (v) => v.toBits(), [0, 0, 0, 0]),
  string: codec(stringFormatter),
  Uri: codec(uriFormatter),
  Version: codec(versionFormatter, NexusVersion.parse, String),
  DateTime: codec(dateTimeFormatter, (j) => NexusDateTime.fromBinary(BigInt(j)), (v) => intJson(v.toBinary()), 0),
  DateTimeOffset: codec(dateTimeOffsetFormatter, (j) => NexusDateTimeOffset.fromUtcTicks(BigInt(j[0]), Number(j[1])), (v) => [intJson(v.utcTicks), v.offsetMinutes], [0, 0]),
  TimeSpan: codec(timeSpanFormatter, (j) => new NexusTimeSpan(BigInt(j)), (v) => intJson(v.ticks), 0),
  DateOnly: codec(dateOnlyFormatter, (j) => new NexusDateOnly(Number(j)), (v) => v.dayNumber, 0),
  TimeOnly: codec(timeOnlyFormatter, (j) => new NexusTimeOnly(BigInt(j)), (v) => intJson(v.ticks), 0),
  Guid: codec(guidFormatter, undefined, undefined, '00000000-0000-0000-0000-000000000000'),
  BigInteger: codec(bigIntegerFormatter, BigInt, String, '0'),
  raw: codec(rawFormatter, fromBase64, toBase64),
};

/**
 * Ext 78 codecs by element type. Float elements go through the typed array's bytes, so NaN payloads survive.
 * @type {Record<string, Codec>}
 */
const EXT78 = (() => {
  /**
   * @param {import('../../../src/serialization/index.js').Formatter<any>} f
   * @param {any} Ctor
   * @param {(j: any) => any} el
   * @param {(v: any) => any} elJson
   * @returns {Codec}
   */
  const simple = (f, Ctor, el, elJson) => codec(f, (j) => Ctor.from(j.map(el)), (v) => Array.from(v, elJson));
  /**
   * @param {import('../../../src/serialization/index.js').Formatter<any>} f
   * @param {any} Ctor
   * @param {number} size
   * @param {string} prefix
   * @returns {Codec}
   */
  const float = (f, Ctor, size, prefix) => codec(
    f,
    (j) => {
      const arr = new Ctor(j.length);
      const view = new DataView(arr.buffer);
      j.forEach((/** @type {any} */ x, /** @type {number} */ i) => {
        if (size === 4) view.setUint32(i * 4, typeof x === 'number' ? (scratch.setFloat32(0, x), scratch.getUint32(0)) : Number(floatBits(x, prefix, 8)), true);
        else view.setBigUint64(i * 8, typeof x === 'number' ? (scratch.setFloat64(0, x), scratch.getBigUint64(0)) : floatBits(x, prefix, 16), true);
      });
      return arr;
    },
    (v) => {
      const view = new DataView(v.buffer, v.byteOffset, v.byteLength);
      return Array.from({ length: v.length }, (_, i) => (size === 4
        ? bitsJson(BigInt(view.getUint32(i * 4, true)), prefix, 8)
        : bitsJson(view.getBigUint64(i * 8, true), prefix, 16)));
    },
  );
  return {
    sbyte: simple(int8ArrayFormatter, Int8Array, Number, Number),
    short: simple(int16ArrayFormatter, Int16Array, Number, Number),
    ushort: simple(uint16ArrayFormatter, Uint16Array, Number, Number),
    int: simple(int32ArrayFormatter, Int32Array, Number, Number),
    uint: simple(uint32ArrayFormatter, Uint32Array, Number, Number),
    long: simple(int64ArrayFormatter, BigInt64Array, BigInt, intJson),
    ulong: simple(uint64ArrayFormatter, BigUint64Array, BigInt, intJson),
    float: float(float32ArrayFormatter, Float32Array, 4, 'f32:0x'),
    double: float(float64ArrayFormatter, Float64Array, 8, 'f64:0x'),
    char: simple(charArrayFormatter, Uint16Array, Number, Number),
    half: simple(halfArrayFormatter, Float32Array, f16FromJson, f16Json),
  };
})();

const BIN_TYPES = new Set(['ReadOnlySequence', 'ArraySegment']);
const LIST_INTERFACES = new Set(['IList', 'IReadOnlyList', 'IEnumerable', 'ICollection', 'IReadOnlyCollection', 'Queue', 'Stack']);
const MAP_TYPES = new Set(['Dictionary', 'IDictionary', 'IReadOnlyDictionary']);

/**
 * Codecs for the type expressions of the vectors, built from the built-in formatters and `schemas.jsonl`.
 */
export class GoldenCodecs {
  /** @type {Map<string, any>} */
  #schemas = new Map();
  /** @type {Map<string, Codec>} */
  #cache = new Map();

  /** @param {string} schemasText The text of `schemas.jsonl`. */
  constructor(schemasText) {
    for (const line of lines(schemasText)) {
      const s = JSON.parse(line);
      this.#schemas.set(s.id, s);
    }
  }

  /** @param {number} messageType @returns {any} */
  messageSchema(messageType) {
    for (const s of this.#schemas.values()) if (s.kind === 'message' && s.messageType === messageType) return s;
    return undefined;
  }

  /** @param {string} text @returns {Codec} */
  get(text) {
    return this.#build(parseType(text));
  }

  /** @param {TypeNode} node @returns {Codec} */
  #build(node) {
    const key = typeKey(node);
    let c = this.#cache.get(key);
    if (!c) {
      c = this.#create(node, key);
      this.#cache.set(key, c);
    }
    return c;
  }

  /**
   * @param {TypeNode} element
   * @param {boolean} binForBytes
   * @returns {Codec}
   */
  #sequence(element, binForBytes) {
    if (element.kind === 'name' && element.args.length === 0) {
      if (element.name === 'byte' && binForBytes) return BIN;
      const ext = EXT78[element.name];
      if (ext) return ext;
    }
    return this.#list(element);
  }

  /** @param {TypeNode} element @returns {Codec} */
  #list(element) {
    const item = this.#build(element);
    return codec(arrayFormatter(item.f), (j) => j.map(item.toJs), (v) => v.map(item.toJson));
  }

  /** @param {TypeNode} node @param {string} key @returns {Codec} */
  #create(node, key) {
    switch (node.kind) {
      case 'nullable': {
        const inner = this.#build(node.inner);
        return codec(nullableFormatter(inner.f), inner.toJs, inner.toJson);
      }
      case 'tuple':
        return this.#tuple(node.items);
      case 'array':
        return this.#sequence(node.element, true);
    }
    const { name, args } = node;
    if (args.length === 0 && PRIMS[name]) return PRIMS[name];
    if (args.length === 1 && (name === 'List')) return this.#sequence(args[0], false);
    if (args.length === 1 && (name === 'Memory' || name === 'ReadOnlyMemory')) return this.#sequence(args[0], true);
    if (args.length === 1 && BIN_TYPES.has(name)) return BIN;
    if (args.length === 1 && LIST_INTERFACES.has(name)) return this.#list(args[0]);
    if (args.length === 1 && name === 'HashSet') {
      const item = this.#build(args[0]);
      return codec(setFormatter(item.f), (j) => new Set(j.map(item.toJs)), (v) => [...v].map(item.toJson));
    }
    if (args.length === 2 && MAP_TYPES.has(name)) {
      const k = this.#build(args[0]);
      const v = this.#build(args[1]);
      return codec(
        mapFormatter(k.f, v.f),
        (j) => new Map(j.map((/** @type {any[]} */ e) => [k.toJs(e[0]), v.toJs(e[1])])),
        (m) => [...m].map(([a, b]) => [k.toJson(a), v.toJson(b)]),
      );
    }
    if (name === 'ValueTuple' || name === 'KeyValuePair') return this.#tuple(args);
    if (name === 'Tuple') {
      const t = this.#tuple(args);
      return codec(nullableFormatter(t.f), t.toJs, t.toJson);
    }
    const schema = this.#schemas.get(name);
    if (!schema) throw new Error(`no JS codec for the type '${key}'`);
    switch (schema.kind) {
      case 'object': return this.#object(schema, args, key);
      case 'union': return this.#union(schema, args, key);
      case 'enum': return this.#build({ kind: 'name', name: schema.underlying, args: [] });
      case 'external': return codec(nullableFormatter(rawFormatter), fromBase64, toBase64);
      case 'message': return this.#message(schema);
      default: throw new Error(`unknown schema kind '${schema.kind}' of '${name}'`);
    }
  }

  /** @param {TypeNode[]} items @returns {Codec} */
  #tuple(items) {
    const codecs = items.map((t) => this.#build(t));
    return codec(
      tupleFormatter(...codecs.map((c) => c.f)),
      (j) => j.map((/** @type {any} */ x, /** @type {number} */ i) => codecs[i].toJs(x)),
      (v) => v.map((/** @type {any} */ x, /** @type {number} */ i) => codecs[i].toJson(x)),
    );
  }

  /**
   * A `[NexusObject]` (§10.2), as J03 generates it: an array of `length` elements by key, nil at unused keys; extra
   * elements are skipped and missing ones stay null. JS holds it as that array. One nesting level.
   * @param {any} schema
   * @param {TypeNode[]} args
   * @param {string} key
   * @returns {Codec}
   */
  #object(schema, args, key) {
    const length = schema.length;
    const struct = STRUCT_OBJECTS.has(schema.id);
    /** @type {(Codec | undefined)[]} */
    const members = new Array(length);
    /** Missing members are C# `default(T)`: null for a reference type. */
    const defaults = () => members.map((m) => (m && m.zero !== undefined ? m.toJs(m.zero) : null));
    /** @type {Codec} */
    const self = {
      f: {
        write: (w, v) => {
          if (v === null) {
            if (struct) throw new TypeError(`${key} is a struct: never null`);
            w.writeNil();
            return;
          }
          w.writeArrayHeader(length);
          for (let i = 0; i < length; i++) {
            const m = members[i];
            if (m) m.f.write(w, v[i]);
            else w.writeNil();
          }
        },
        read: (r) => {
          if (!struct && r.tryReadNil()) return null;
          r.enter();
          const n = r.readArrayHeader();
          const result = defaults();
          for (let i = 0; i < n; i++) {
            const m = i < length ? members[i] : undefined;
            if (m) result[i] = m.f.read(r);
            else r.skip();
          }
          r.exit();
          return result;
        },
      },
      toJs: (j) => (j === null ? null : j.map((/** @type {any} */ x, /** @type {number} */ i) => (members[i] && x !== null ? /** @type {Codec} */ (members[i]).toJs(x) : null))),
      toJson: (v) => (v === null ? null : v.map((/** @type {any} */ x, /** @type {number} */ i) => (members[i] && x !== null ? /** @type {Codec} */ (members[i]).toJson(x) : null))),
    };
    if (struct) {
      Object.defineProperty(self, 'zero', { get: () => members.map((m) => (m && m.zero !== undefined ? m.zero : null)), enumerable: true });
    }
    // Registered before the members are built: an object may contain itself (InteropNested).
    this.#cache.set(key, self);
    for (const [k, , type] of schema.members) members[k] = this.#build(bind(parseType(type), args));
    return self;
  }

  /**
   * A union (§10.3): `[tag: uint16, value]`, or nil for null. An unknown tag is refused (`value`). One nesting level.
   * @param {any} schema
   * @param {TypeNode[]} args
   * @param {string} key
   * @returns {Codec}
   */
  #union(schema, args, key) {
    /** @type {Map<number, Codec>} */
    const cases = new Map();
    /** @type {Codec} */
    const self = {
      f: {
        write: (w, v) => {
          if (v === null) {
            w.writeNil();
            return;
          }
          const c = cases.get(v[0]);
          if (!c) throw new RangeError(`${key} has no case ${v[0]}`);
          w.writeArrayHeader(2);
          w.writeInt(v[0]);
          c.f.write(w, v[1]);
        },
        read: (r) => {
          if (r.tryReadNil()) return null;
          r.enter();
          r.readArrayHeader(2);
          const tag = r.readUInt16();
          const c = cases.get(tag);
          if (!c) throw new NexusSerializationError('value', `${key} has no case ${tag}`);
          const value = c.f.read(r);
          r.exit();
          return [tag, value];
        },
      },
      toJs: (j) => (j === null ? null : [j[0], /** @type {Codec} */ (cases.get(j[0])).toJs(j[1])]),
      toJson: (v) => (v === null ? null : [v[0], /** @type {Codec} */ (cases.get(v[0])).toJson(v[1])]),
    };
    this.#cache.set(key, self);
    for (const [tag, caseId] of schema.cases) cases.set(tag, this.#build({ kind: 'name', name: caseId, args }));
    return self;
  }

  /**
   * A protocol message body (§4), as the session codec reads it: the fields present are read first, then the count
   * is checked against `minCount`/`maxCount` (`count`). With `maxCount: null`, extra fields are skipped and missing
   * ones are 0. A nil `byte[]` field is an empty token; a `raw` field is one embedded value (nil is null).
   * @param {any} schema
   * @returns {Codec}
   */
  #message(schema) {
    /** @type {Codec[]} */
    const fields = schema.fields.map((/** @type {[string, string]} */ [, type]) => {
      if (type === 'byte[]') {
        return codec({
          write: (w, v) => w.writeBin(v),
          read: (r) => r.readBin() ?? new Uint8Array(0),
        }, fromBase64, toBase64);
      }
      if (type === 'raw') {
        return codec({
          write: (w, v) => (v === null ? w.writeNil() : w.writeRaw(v)),
          read: (r) => (r.tryReadNil() ? null : r.readRaw()),
        }, fromBase64, toBase64);
      }
      return this.get(type);
    });
    const { minCount, maxCount } = schema;
    return {
      f: {
        write: (w, v) => {
          w.writeArrayHeader(v.length);
          for (let i = 0; i < v.length; i++) fields[i].f.write(w, v[i]);
        },
        read: (r) => {
          const count = r.readArrayHeader();
          const known = Math.min(count, fields.length);
          const result = [];
          for (let i = 0; i < known; i++) result.push(fields[i].f.read(r));
          for (let i = known; i < count; i++) r.skip();
          if (count < minCount || (maxCount !== null && count > maxCount)) {
            throw new NexusSerializationError('count', `${schema.id} has ${minCount} to ${maxCount ?? 'any'} fields; got ${count}`);
          }
          if (maxCount === null) for (let i = known; i < fields.length; i++) result.push(fields[i].toJs(0));
          if (schema.id === 'RemoteErrorDetail') {
            if ((result[1]?.length ?? 0) > 256) throw new NexusSerializationError('value', 'the type is at most 256 chars');
            if ((result[2]?.length ?? 0) > 2048) throw new NexusSerializationError('value', 'the message is at most 2048 chars');
          }
          return result;
        },
      },
      toJs: (j) => j.map((/** @type {any} */ x, /** @type {number} */ i) => fields[i].toJs(x)),
      toJson: (v) => v.map((/** @type {any} */ x, /** @type {number} */ i) => fields[i].toJson(x)),
    };
  }
}

// ------------------------------------------------------------------ Frames (§1, §2)

const PREAMBLE_MAGIC = [0x4e, 0x6e, 0x50, 0x14];

/** @param {number} type @returns {boolean} */
function hasNoBody(type) {
  return type === 1 || (type >= 20 && type <= 38);
}

/**
 * @param {number} type
 * @param {Uint8Array} post Post-header.
 * @param {Uint8Array} body
 * @returns {Uint8Array}
 */
function frameBytes(type, post, body) {
  const out = new Uint8Array(3 + post.length + body.length);
  out[0] = type;
  new DataView(out.buffer).setUint16(1, body.length, true);
  out.set(post, 3);
  out.set(body, 3 + post.length);
  return out;
}

/**
 * Encodes a frame value (README "Frames").
 * @param {any[]} value
 * @param {GoldenCodecs} codecs
 * @returns {Uint8Array}
 */
function encodeFrame(value, codecs) {
  const type = value[0];
  if (hasNoBody(type)) return Uint8Array.of(type);
  switch (type) {
    case 50: return frameBytes(type, Uint8Array.of(value[1], value[2]), fromBase64(value[3]));
    case 51: {
      const body = new Uint8Array(4);
      new DataView(body.buffer).setUint32(0, value[3], true);
      return frameBytes(type, Uint8Array.of(value[1], value[2]), body);
    }
    case 114: return frameBytes(type, Uint8Array.of(value[1] & 0xff, value[1] >> 8, value[2]), fromBase64(value[3]));
    default: {
      const schema = codecs.messageSchema(type);
      if (!schema) throw new Error(`no message has frame type ${type}`);
      const c = codecs.get(schema.id);
      return frameBytes(type, new Uint8Array(0), encode(c, c.toJs(value[1])));
    }
  }
}

/**
 * Decodes one complete frame.
 * @param {Uint8Array} bytes
 * @param {GoldenCodecs} codecs
 * @returns {any[]}
 */
function decodeFrame(bytes, codecs) {
  const type = bytes[0];
  if (hasNoBody(type)) {
    if (bytes.length !== 1) throw new Error('trailing bytes after a frame without a body');
    return [type];
  }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const length = view.getUint16(1, true);
  const postLength = type === 50 || type === 51 ? 2 : type === 114 ? 3 : 0;
  if (bytes.length !== 3 + postLength + length) throw new Error('the frame length does not match its header');
  const body = bytes.subarray(3 + postLength);
  switch (type) {
    case 50: return [type, bytes[3], bytes[4], toBase64(body)];
    case 51: return [type, bytes[3], bytes[4], view.getUint32(5, true)];
    case 114: return [type, view.getUint16(3, true), bytes[5], toBase64(body)];
    default: {
      const schema = codecs.messageSchema(type);
      if (!schema) throw new Error(`no message has frame type ${type}`);
      const c = codecs.get(schema.id);
      return [type, c.toJson(decode(c, body))];
    }
  }
}

// ------------------------------------------------------------------ Replay

/**
 * @param {Vector['options']} options
 * @returns {import('../../../src/serialization/index.js').SerializerOptions}
 */
function readerOptions(options) {
  return {
    preset: options?.security ?? 'untrusted',
    ...(options?.maxDepth !== undefined ? { maxDepth: options.maxDepth } : {}),
    ...(options?.strictUtf8 !== undefined ? { strictUtf8: options.strictUtf8 } : {}),
    ...(options?.maxElements !== undefined ? { maxElements: options.maxElements } : {}),
  };
}

/** @param {Codec} c @param {any} value @returns {Uint8Array} */
function encode(c, value) {
  const w = new MsgPackWriter({ initialCapacity: 16 });
  c.f.write(w, value);
  return w.toUint8Array().slice();
}

/**
 * Decodes exactly one value from all of `source`.
 * @param {Codec} c
 * @param {Uint8Array | Uint8Array[]} source
 * @param {Vector['options']} [options]
 * @returns {any}
 */
function decode(c, source, options) {
  const r = new MsgPackReader(source, readerOptions(options));
  const value = c.f.read(r);
  r.ensureEnd();
  return value;
}

/**
 * Splits `bytes` into small segments, so every read crosses segment boundaries.
 * @param {Uint8Array} bytes
 * @returns {Uint8Array[]}
 */
function segments(bytes) {
  const size = bytes.length <= 4096 ? 1 : 7;
  /** @type {Uint8Array[]} */
  const out = [new Uint8Array(0)];
  for (let i = 0; i < bytes.length; i += size) out.push(bytes.subarray(i, i + size), new Uint8Array(0));
  return out;
}

/**
 * Decodes `source` and returns the typed JSON of the value.
 * @param {Codec} c
 * @param {Uint8Array | Uint8Array[]} source
 * @param {Vector['options']} options
 * @returns {any}
 */
function decodeJson(c, source, options) {
  return c.toJson(decode(c, source, options));
}

/**
 * Replays one vector (README "Modes"); a failure throws an assertion error.
 * @param {Vector} vector
 * @param {GoldenCodecs} codecs
 * @returns {void}
 */
export function replay(vector, codecs) {
  const bytes = fromHex(vector.hex);
  const where = `${vector.file}: ${vector.id} (${vector.type})`;
  if (vector.type === 'preamble') {
    const encoded = Uint8Array.of(...PREAMBLE_MAGIC, 0, 0, 0, vector.value[0]);
    expect(toHex(encoded), where).toBe(vector.hex);
    expect(bytes.length === 8 && PREAMBLE_MAGIC.every((b, i) => bytes[i] === b) && bytes[4] === 0 && bytes[5] === 0 && bytes[6] === 0, where).toBe(true);
    expect([bytes[7]], where).toEqual(vector.value);
    return;
  }
  if (vector.type === 'frame') {
    expect(vector.mode, where).toBe('both');
    expect(toHex(encodeFrame(vector.value, codecs)), where).toBe(vector.hex);
    expect(jsonEquals(decodeFrame(bytes, codecs), vector.value), where).toBe(true);
    return;
  }

  const c = codecs.get(vector.type);
  switch (vector.mode) {
    case 'both': {
      const encoded = encode(c, c.toJs(vector.value));
      if (toHex(encoded) !== vector.hex) {
        // A NaN whose bits a JS number cannot hold may encode differently; it must still read back as a NaN.
        expect(containsNaN(vector.value), `${where}: encoded ${toHex(encoded)}`).toBe(true);
        expect(jsonEquals(decodeJson(c, encoded, vector.options), vector.value), `${where}: re-read of ${toHex(encoded)}`).toBe(true);
      }
      checkDecode(c, vector, bytes, where);
      break;
    }
    case 'decode': {
      checkDecode(c, vector, bytes, where);
      // The value is canonical: it encodes to other bytes that read back to the same value.
      const canonical = encode(c, c.toJs(vector.value));
      expect(toHex(canonical), where).not.toBe(vector.hex);
      expect(jsonEquals(decodeJson(c, canonical, undefined), vector.value), `${where}: canonical ${toHex(canonical)}`).toBe(true);
      break;
    }
    case 'reject':
      checkReject(c, vector, bytes, where);
      checkReject(c, vector, segments(bytes), `${where} [segmented]`);
      break;
    default:
      throw new Error(`${where}: unknown mode '${vector.mode}'`);
  }
  if (vector.type === 'CollectionListMessage') replayCollectionCodec(vector, bytes, c, where);
}

/** Embedded values as their raw bytes, nil as null: the collection codec under test does not interpret them. */
const rawOrNil = nullableFormatter(rawFormatter);

/** The runtime's collection item codec (J09, `src/collections/messages.js`), with raw item values. */
const collectionCodec = listMessageFormatter(/** @type {any} */ (rawOrNil), /** @type {any} */ (rawOrNil));

/**
 * Replays a collection item vector through the runtime's collection codec too: it reads the bytes and writes the
 * canonical bytes again, and it refuses every reject vector.
 * @param {Vector} vector
 * @param {Uint8Array} bytes
 * @param {Codec} c The schema codec of `CollectionListMessage`.
 * @param {string} where
 */
function replayCollectionCodec(vector, bytes, c, where) {
  const read = () => {
    const r = new MsgPackReader(bytes, readerOptions(vector.options));
    const message = collectionCodec.read(/** @type {any} */ (r));
    r.ensureEnd();
    return message;
  };
  if (vector.mode === 'reject') {
    expect(read, `${where} [collection codec]`).toThrow();
    return;
  }
  const w = new MsgPackWriter();
  collectionCodec.write(/** @type {any} */ (w), read());
  expect(toHex(w.toUint8Array()), `${where} [collection codec]`).toBe(toHex(encode(c, c.toJs(vector.value))));
}

/**
 * @param {Codec} c
 * @param {Vector} vector
 * @param {Uint8Array} bytes
 * @param {string} where
 */
function checkDecode(c, vector, bytes, where) {
  const json = decodeJson(c, bytes, vector.options);
  if (!jsonEquals(json, vector.value)) expect(json, where).toEqual(vector.value);
  const segmented = decodeJson(c, segments(bytes), vector.options);
  if (!jsonEquals(segmented, vector.value)) expect(segmented, `${where} [segmented]`).toEqual(vector.value);
}

/** `length` and `truncated` may be reported as each other (README "Error kinds"). */
const EQUIVALENT_KINDS = new Set(['length', 'truncated']);

/**
 * @param {Codec} c
 * @param {Vector} vector
 * @param {Uint8Array | Uint8Array[]} source
 * @param {string} where
 */
function checkReject(c, vector, source, where) {
  let error;
  try {
    decode(c, source, vector.options);
  } catch (e) {
    error = e;
  }
  expect(error, `${where}: decoded without an error`).toBeInstanceOf(NexusSerializationError);
  const kind = /** @type {NexusSerializationError} */ (error).kind;
  if (vector.error !== undefined && kind !== vector.error && !(EQUIVALENT_KINDS.has(kind) && EQUIVALENT_KINDS.has(vector.error))) {
    expect(`${kind}: ${/** @type {Error} */ (error).message}`, where).toBe(vector.error);
  }
}
