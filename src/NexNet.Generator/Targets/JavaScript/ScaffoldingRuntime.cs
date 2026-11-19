namespace NexNet.Generator.Targets.JavaScript;

/// <summary>
/// The fixed modules of the JavaScript test scaffolding (J11), written into its <c>scaffolding/</c> directory beside the
/// generated ones (<see cref="JsScaffoldingEmitter"/>): <c>typed-json.js</c> (typed JSON to and from MessagePack, the
/// port of the C# and Java <c>TypedJson</c>) and <c>scaffolding-runtime.js</c> (<c>TypedCodec</c>, <c>ScriptCall</c>,
/// <c>runScript</c>, <c>ScaffoldingExport</c>), each with its declarations. <see cref="RuntimePlaceholder"/> stands for
/// the runtime module specifier.
/// </summary>
internal static class JsScaffoldingRuntime
{
    /// <summary>Replaced with the quoted runtime module (<c>'@dtronix/nexnet'</c> unless <c>ModuleName</c> is set).</summary>
    public const string RuntimePlaceholder = "'$RUNTIME$'";

    /// <summary>The fixed files: path in the scaffolding directory, content.</summary>
    public static readonly (string Name, string Text)[] Files =
    {
        ("typed-json.js", TypedJsonJs),
        ("typed-json.d.ts", TypedJsonDts),
        ("scaffolding-runtime.js", ScaffoldingRuntimeJs),
        ("scaffolding-runtime.d.ts", ScaffoldingRuntimeDts),
    };

    /// <summary>The fixed file names.</summary>
    public static IEnumerable<string> Names => Files.Select(f => f.Name);

    private const string TypedJsonJs = """
/**
 * Typed JSON: the value model of the golden vectors and of the interop control protocol (`tests/golden/README.md`,
 * "Typed JSON"). Converts between one NexNet MessagePack value and its typed JSON, guided by the value's shape (the IR
 * type reference). The JavaScript port of the C# and Java `TypedJson`.
 *
 * JSON values are what `JSON.parse` gives: `null`, booleans, numbers, strings and arrays. Output integers are numbers
 * within ±(2^53 - 1) and decimal strings beyond; floats are bit-pattern strings (`"f32:0x3F800000"`). On input an
 * integral number, a bigint or a decimal string is an integer, and a plain number is accepted for a float.
 * @module
 */

import { MsgPackReader, MsgPackWriter } from '$RUNTIME$';

/** Largest integer carried as a JSON number (2^53 - 1). */
export const MAX_SAFE_INTEGER = 9007199254740991;

const PRIMITIVE_ARRAY_EXT = 78;
const TRUSTED = Object.freeze({ preset: 'trusted' });
const INT64_MIN = -(1n << 63n);
const INT64_MAX = (1n << 63n) - 1n;
const UINT64_MAX = (1n << 64n) - 1n;

/** The kinds of a type shape (the IR `TypeRefKind`). */
export const Kind = Object.freeze({
  PRIM: 'PRIM',
  BIN: 'BIN',
  PRIM_ARRAY: 'PRIM_ARRAY',
  SEQ: 'SEQ',
  SET: 'SET',
  QUEUE: 'QUEUE',
  STACK: 'STACK',
  MAP: 'MAP',
  TUPLE: 'TUPLE',
  NULLABLE: 'NULLABLE',
  DECL: 'DECL',
  TYPE_PARAM: 'TYPE_PARAM',
  EXTERNAL: 'EXTERNAL',
});

/** The built-in leaf types (the IR `PrimKind`). */
export const Prim = Object.freeze({
  BOOL: 'BOOL',
  SBYTE: 'SBYTE',
  BYTE: 'BYTE',
  INT16: 'INT16',
  UINT16: 'UINT16',
  INT32: 'INT32',
  UINT32: 'UINT32',
  INT64: 'INT64',
  UINT64: 'UINT64',
  SINGLE: 'SINGLE',
  DOUBLE: 'DOUBLE',
  HALF: 'HALF',
  CHAR: 'CHAR',
  DECIMAL: 'DECIMAL',
  STRING: 'STRING',
  DATE_TIME: 'DATE_TIME',
  DATE_TIME_OFFSET: 'DATE_TIME_OFFSET',
  TIME_SPAN: 'TIME_SPAN',
  DATE_ONLY: 'DATE_ONLY',
  TIME_ONLY: 'TIME_ONLY',
  GUID: 'GUID',
  BIG_INTEGER: 'BIG_INTEGER',
  URI: 'URI',
  VERSION: 'VERSION',
});

/**
 * @typedef {object} Type The shape of a serialized type (the IR `TypeRef`).
 * @property {string} kind A {@link Kind}.
 * @property {string | null} prim The leaf (`PRIM`) or element (`PRIM_ARRAY`), a {@link Prim}.
 * @property {string | null} clr The .NET container name (`List`, `Array`, `ValueTuple`…) or external type name.
 * @property {readonly Type[]} args Element, key/value, tuple element, nullable inner or type argument shapes.
 * @property {string | null} declId The declared type id (`DECL`).
 * @property {number} ordinal The type parameter position (`TYPE_PARAM`).
 */

/**
 * @param {string} kind @param {string | null} p @param {string | null} clr @param {readonly Type[]} args
 * @param {string | null} declId @param {number} ordinal @returns {Type}
 */
function shape(kind, p, clr, args, declId, ordinal) {
  return Object.freeze({ kind, prim: p, clr, args: Object.freeze([...args]), declId, ordinal });
}

/** @param {string} p @returns {string} */
function checkPrim(p) {
  if (!Object.prototype.hasOwnProperty.call(Prim, p)) throw new TypeError("Unknown primitive '" + String(p) + "'.");
  return p;
}

/** A built-in leaf. @param {string} p A {@link Prim}. @returns {Type} */
export function prim(p) {
  return shape(Kind.PRIM, checkPrim(p), null, [], null, 0);
}

/** Bytes written as bin. @returns {Type} */
export function bin() {
  return shape(Kind.BIN, null, null, [], null, 0);
}

/** An ext 78 primitive array. @param {string} element A {@link Prim}. @returns {Type} */
export function primArray(element) {
  return shape(Kind.PRIM_ARRAY, checkPrim(element), null, [], null, 0);
}

/**
 * An element-wise sequence.
 * @param {string | null} clr The .NET container (`Array`, `List`, `IReadOnlyList`…), or null.
 * @param {Type} element
 * @returns {Type}
 */
export function seq(clr, element) {
  return shape(Kind.SEQ, null, clr, [element], null, 0);
}

/** A set. @param {Type} element @returns {Type} */
export function set(element) {
  return shape(Kind.SET, null, null, [element], null, 0);
}

/** A queue, front first. @param {Type} element @returns {Type} */
export function queue(element) {
  return shape(Kind.QUEUE, null, null, [element], null, 0);
}

/** A stack, top first. @param {Type} element @returns {Type} */
export function stack(element) {
  return shape(Kind.STACK, null, null, [element], null, 0);
}

/** A map. @param {Type} key @param {Type} value @returns {Type} */
export function map(key, value) {
  return shape(Kind.MAP, null, null, [key, value], null, 0);
}

/**
 * A tuple.
 * @param {string} clr `ValueTuple`, `Tuple` or `KeyValuePair`.
 * @param {...Type} elements
 * @returns {Type}
 */
export function tuple(clr, ...elements) {
  return shape(Kind.TUPLE, null, clr, elements, null, 0);
}

/** A nullable value type. @param {Type} inner @returns {Type} */
export function nullable(inner) {
  return shape(Kind.NULLABLE, null, null, [inner], null, 0);
}

/** A declared type. @param {string} id The declared type id. @param {...Type} typeArgs @returns {Type} */
export function decl(id, ...typeArgs) {
  return shape(Kind.DECL, null, null, typeArgs, id, 0);
}

/** A type parameter of the enclosing declared type. @param {number} ordinal @returns {Type} */
export function typeParam(ordinal) {
  return shape(Kind.TYPE_PARAM, null, null, [], null, ordinal);
}

/** A type with a user formatter: its raw MessagePack value. @param {string} clr The .NET type name. @returns {Type} */
export function external(clr) {
  return shape(Kind.EXTERNAL, null, clr, [], null, 0);
}

/**
 * The shape with the type parameters replaced by `typeArgs`.
 * @param {Type} type
 * @param {readonly Type[]} typeArgs
 * @returns {Type}
 */
export function bind(type, typeArgs) {
  if (type.kind === Kind.TYPE_PARAM) {
    if (type.ordinal >= typeArgs.length) throw new TypeError('Unbound type parameter T' + type.ordinal + '.');
    return typeArgs[type.ordinal];
  }
  if (type.args.length === 0 || typeArgs.length === 0) return type;
  return shape(type.kind, type.prim, type.clr, type.args.map((a) => bind(a, typeArgs)), type.declId, type.ordinal);
}

/** A readable form of a shape, for messages. @param {Type} type @returns {string} */
export function describe(type) {
  return type.kind + (type.prim === null ? '' : ' ' + type.prim) + (type.clr === null ? '' : ' ' + type.clr)
    + (type.declId === null ? '' : ' ' + type.declId) + (type.args.length === 0 ? '' : ' [' + type.args.map(describe).join(', ') + ']');
}

// ------------------------------------------------------------------ declared types

/**
 * @typedef {object} Member One keyed member of an object.
 * @property {number} key
 * @property {Type} type
 */

/**
 * @typedef {object} ObjectDecl A `[NexusObject]` class or struct: an array by key, nil in the gaps.
 * @property {'object'} declKind
 * @property {string} id The .NET full metadata name.
 * @property {number} length The array length.
 * @property {boolean} struct True for a C# struct (nil is not a value of it).
 * @property {readonly (Type | null)[]} members The member shape at each key; null for a gap.
 */

/**
 * @typedef {object} UnionDecl A union: `[tag, value]`.
 * @property {'union'} declKind
 * @property {string} id
 * @property {ReadonlyMap<number, string>} cases The declared type of each case, by tag.
 */

/**
 * @typedef {object} EnumDecl An enum: its underlying integer.
 * @property {'enum'} declKind
 * @property {string} id
 * @property {string} underlying A {@link Prim}.
 */

/** @typedef {ObjectDecl | UnionDecl | EnumDecl} Decl */

/** A member of an object. @param {number} key @param {Type} type @returns {Member} */
export function member(key, type) {
  return Object.freeze({ key, type });
}

/**
 * An object.
 * @param {string} id
 * @param {number} length Its array length.
 * @param {boolean} struct True for a C# struct.
 * @param {...Member} members Its members, including those of its base types.
 * @returns {ObjectDecl}
 */
export function object(id, length, struct, ...members) {
  /** @type {(Type | null)[]} */
  const byKey = new Array(length).fill(null);
  for (const m of members) {
    if (m.key < 0 || m.key >= length) throw new RangeError('Member key ' + m.key + " is outside '" + id + "'.");
    byKey[m.key] = m.type;
  }
  return Object.freeze({ declKind: /** @type {'object'} */ ('object'), id, length, struct, members: Object.freeze(byKey) });
}

/**
 * A union.
 * @param {string} id
 * @param {...(number | string)} cases Alternating tags and case ids: `0, 'Ns.Circle', 1, 'Ns.Square'`.
 * @returns {UnionDecl}
 */
export function union(id, ...cases) {
  /** @type {Map<number, string>} */
  const byTag = new Map();
  for (let i = 0; i + 1 < cases.length; i += 2) byTag.set(Number(cases[i]), String(cases[i + 1]));
  return Object.freeze({ declKind: /** @type {'union'} */ ('union'), id, cases: byTag });
}

/** An enum. @param {string} id @param {string} underlying A {@link Prim}. @returns {EnumDecl} */
export function enumOf(id, underlying) {
  return Object.freeze({ declKind: /** @type {'enum'} */ ('enum'), id, underlying: checkPrim(underlying) });
}

/** The declared types a shape may name. */
export class Schemas {
  /** @type {Map<string, Decl>} */
  #byId = new Map();

  /** @param {...Decl} decls */
  constructor(...decls) {
    for (const d of decls) this.#byId.set(d.id, d);
  }

  /**
   * The declaration of `id`; throws when there is none.
   * @param {string} id
   * @returns {Decl}
   */
  find(id) {
    const d = this.#byId.get(id);
    if (d === undefined) throw new TypeError("Unknown declared type '" + id + "'.");
    return d;
  }
}

// ------------------------------------------------------------------ entry points

/**
 * Converts one MessagePack value to typed JSON.
 * @param {Uint8Array} data The value's bytes.
 * @param {Type} type Its shape.
 * @param {Schemas} schemas The declared types.
 * @returns {unknown} The typed JSON.
 */
export function fromMsgPack(data, type, schemas) {
  const r = new MsgPackReader(data, TRUSTED);
  const value = read(r, type, schemas);
  if (r.remaining !== 0) throw new TypeError('Trailing bytes after the ' + describe(type) + ' value.');
  return value;
}

/**
 * Converts typed JSON to one MessagePack value.
 * @param {unknown} value The typed JSON.
 * @param {Type} type Its shape.
 * @param {Schemas} schemas The declared types.
 * @returns {Uint8Array} The bytes.
 */
export function toMsgPack(value, type, schemas) {
  const w = new MsgPackWriter();
  write(w, value, type, schemas);
  return w.toUint8Array().slice();
}

/**
 * The typed JSON of the .NET default value of a shape: zero for numbers, enums and the other value types, the defaults
 * of every member for a struct, null for reference types.
 * @param {Type} type
 * @param {Schemas} schemas
 * @returns {unknown}
 */
export function defaultValue(type, schemas) {
  switch (type.kind) {
    case Kind.PRIM:
      return defaultPrim(primOf(type));
    case Kind.TUPLE:
      if (type.clr !== 'ValueTuple' && type.clr !== 'KeyValuePair') return null;
      return type.args.map((a) => defaultValue(a, schemas));
    case Kind.DECL: {
      const d = schemas.find(declIdOf(type));
      if (d.declKind === 'enum') return 0;
      if (d.declKind === 'object' && d.struct) {
        return d.members.map((m) => (m === null ? null : defaultValue(bind(m, type.args), schemas)));
      }
      return null;
    }
    default:
      return null;
  }
}

/** @param {string} p @returns {unknown} */
function defaultPrim(p) {
  switch (p) {
    case Prim.BOOL:
      return false;
    case Prim.SINGLE:
      return 'f32:0x00000000';
    case Prim.DOUBLE:
      return 'f64:0x0000000000000000';
    case Prim.HALF:
      return 'f16:0x0000';
    case Prim.DECIMAL:
      return [0, 0, 0, 0];
    case Prim.DATE_TIME_OFFSET:
      return [0, 0];
    case Prim.GUID:
      return '00000000-0000-0000-0000-000000000000';
    case Prim.BIG_INTEGER:
      return '0';
    case Prim.STRING:
    case Prim.URI:
    case Prim.VERSION:
      return null;
    default:
      return 0;
  }
}

// ------------------------------------------------------------------ MessagePack -> JSON

/** @param {MsgPackReader} r @param {Type} s @param {Schemas} schemas @returns {unknown} */
function read(r, s, schemas) {
  if (s.kind === Kind.TYPE_PARAM) throw new TypeError('Unbound type parameter T' + s.ordinal + '.');
  if (r.tryReadNil()) return null;

  switch (s.kind) {
    case Kind.PRIM:
      return readPrim(r, primOf(s));
    case Kind.BIN:
      return base64(r.readBin() ?? new Uint8Array(0));
    case Kind.PRIM_ARRAY:
      return readPrimArray(r, primOf(s));
    case Kind.SEQ: {
      const wire = boundWireForm(s);
      if (wire === Kind.PRIM_ARRAY) return readPrimArray(r, primOf(s.args[0]));
      if (wire === Kind.BIN) return base64(r.readBin() ?? new Uint8Array(0));
      return readArray(r, arg(s, 0), schemas);
    }
    case Kind.SET:
    case Kind.QUEUE:
    case Kind.STACK:
      return readArray(r, arg(s, 0), schemas);
    case Kind.MAP: {
      const count = r.readMapHeader();
      const out = [];
      for (let i = 0; i < count; i++) {
        const key = read(r, arg(s, 0), schemas);
        out.push([key, read(r, arg(s, 1), schemas)]);
      }
      return out;
    }
    case Kind.TUPLE: {
      const count = r.readArrayHeader();
      const out = [];
      for (let i = 0; i < count; i++) {
        if (i < s.args.length) {
          out.push(read(r, s.args[i], schemas));
        } else {
          r.skip();
          out.push(null);
        }
      }
      return out;
    }
    case Kind.NULLABLE:
      return read(r, arg(s, 0), schemas);
    case Kind.DECL:
      return readDecl(r, schemas.find(declIdOf(s)), s.args, schemas);
    case Kind.EXTERNAL:
      return base64(r.readRaw());
    default:
      throw new TypeError('Unknown shape kind ' + s.kind + '.');
  }
}

/** @param {MsgPackReader} r @param {Type} element @param {Schemas} schemas @returns {unknown[]} */
function readArray(r, element, schemas) {
  const count = r.readArrayHeader();
  const out = [];
  for (let i = 0; i < count; i++) out.push(read(r, element, schemas));
  return out;
}

/** @param {MsgPackReader} r @param {Decl} d @param {readonly Type[]} typeArgs @param {Schemas} schemas @returns {unknown} */
function readDecl(r, d, typeArgs, schemas) {
  if (d.declKind === 'object') {
    const count = r.readArrayHeader();
    const out = [];
    for (let i = 0; i < count; i++) {
      const m = i < d.members.length ? d.members[i] : null;
      if (m !== null) {
        out.push(read(r, bind(m, typeArgs), schemas));
      } else {
        r.skip();
        out.push(null);
      }
    }
    return out;
  }
  if (d.declKind === 'union') {
    r.readArrayHeader(2);
    const tag = r.readUInt16();
    const caseId = d.cases.get(tag);
    if (caseId === undefined) throw new TypeError("Union '" + d.id + "' has no case " + tag + '.');
    return [tag, read(r, decl(caseId, ...typeArgs), schemas)];
  }
  return readPrim(r, d.underlying);
}

/** @param {MsgPackReader} r @param {string} p @returns {unknown} */
function readPrim(r, p) {
  switch (p) {
    case Prim.BOOL:
      return r.readBool();
    case Prim.SBYTE:
    case Prim.BYTE:
    case Prim.INT16:
    case Prim.UINT16:
    case Prim.INT32:
    case Prim.UINT32:
    case Prim.INT64:
    case Prim.DATE_TIME:
    case Prim.TIME_SPAN:
    case Prim.TIME_ONLY:
      return integer(r.readInt64());
    case Prim.CHAR:
      return r.readUInt16();
    case Prim.DATE_ONLY:
      return r.readInt32();
    case Prim.UINT64:
      return integer(r.readUInt64());
    case Prim.SINGLE:
      return single(readSingleBits(r));
    case Prim.DOUBLE:
      return doubleBits(readDoubleBits(r));
    case Prim.HALF:
      return half(singleToHalfBits(readSingleBits(r)));
    case Prim.STRING:
    case Prim.URI:
    case Prim.VERSION:
      return r.readString();
    case Prim.DATE_TIME_OFFSET: {
      r.readArrayHeader(2);
      const utcTicks = r.readInt64();
      return [integer(utcTicks), r.readInt16()];
    }
    case Prim.DECIMAL: {
      const bytes = readFixedBin(r, 16, 'decimal');
      const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      return [view.getInt32(0, true), view.getInt32(4, true), view.getInt32(8, true), view.getInt32(12, true)];
    }
    case Prim.GUID:
      return guid(readFixedBin(r, 16, 'Guid'));
    case Prim.BIG_INTEGER: {
      const bytes = r.readBytes(r.readBinHeader());
      return bytes.length === 0 ? '0' : fromTwosComplementLE(bytes).toString();
    }
    default:
      throw new TypeError('Unknown primitive ' + p + '.');
  }
}

/** @param {MsgPackReader} r @param {number} length @param {string} what @returns {Uint8Array} */
function readFixedBin(r, length, what) {
  const actual = r.readBinHeader();
  if (actual !== length) throw new TypeError(what + ' must be ' + length + ' bytes.');
  return r.readBytes(length);
}

/**
 * The float32 bits of a float value; the bytes of a float32 are kept as they are (NaN payloads included).
 * @param {MsgPackReader} r
 * @returns {number}
 */
function readSingleBits(r) {
  const raw = r.readRaw();
  const view = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
  if (raw[0] === 0xca && raw.length === 5) return view.getUint32(1);
  if (raw[0] === 0xcb && raw.length === 9) return singleBitsOf(view.getFloat64(1));
  return singleBitsOf(new MsgPackReader(raw, TRUSTED).readFloat());
}

/**
 * The float64 bits of a float value; the bytes of a float64 are kept as they are (NaN payloads included).
 * @param {MsgPackReader} r
 * @returns {bigint}
 */
function readDoubleBits(r) {
  const raw = r.readRaw();
  const view = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
  if (raw[0] === 0xcb && raw.length === 9) return view.getBigUint64(1);
  if (raw[0] === 0xca && raw.length === 5) return doubleBitsOf(singleOfBits(view.getUint32(1)));
  return doubleBitsOf(new MsgPackReader(raw, TRUSTED).readFloat());
}

/** @param {MsgPackReader} r @param {string} element @returns {unknown[]} */
function readPrimArray(r, element) {
  const { type, length } = r.readExtHeader();
  if (type !== PRIMITIVE_ARRAY_EXT) throw new TypeError('Expected ext ' + PRIMITIVE_ARRAY_EXT + ', got ext ' + type + '.');
  if (length < 1) throw new TypeError('Primitive array without a kind byte.');
  const bytes = r.readBytes(length);
  if (bytes[0] !== primitiveKind(element)) throw new TypeError('Primitive array kind ' + bytes[0] + ' does not match ' + element + '.');
  const size = elementSize(element);
  if ((length - 1) % size !== 0) throw new TypeError('Primitive array length is not a multiple of the element size.');
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const out = [];
  for (let offset = 1; offset < length; offset += size) out.push(readElement(element, view, offset));
  return out;
}

/** @param {string} element @param {DataView} v @param {number} offset @returns {unknown} */
function readElement(element, v, offset) {
  switch (element) {
    case Prim.SBYTE:
      return v.getInt8(offset);
    case Prim.INT16:
      return v.getInt16(offset, true);
    case Prim.UINT16:
    case Prim.CHAR:
      return v.getUint16(offset, true);
    case Prim.INT32:
      return v.getInt32(offset, true);
    case Prim.UINT32:
      return v.getUint32(offset, true);
    case Prim.INT64:
      return integer(v.getBigInt64(offset, true));
    case Prim.UINT64:
      return integer(v.getBigUint64(offset, true));
    case Prim.SINGLE:
      return single(v.getUint32(offset, true));
    case Prim.DOUBLE:
      return doubleBits(v.getBigUint64(offset, true));
    case Prim.HALF:
      return half(v.getUint16(offset, true));
    default:
      throw new TypeError(element + ' is not a primitive array element.');
  }
}

// ------------------------------------------------------------------ JSON -> MessagePack

/** @param {MsgPackWriter} w @param {unknown} v @param {Type} s @param {Schemas} schemas @returns {void} */
function write(w, v, s, schemas) {
  if (s.kind === Kind.TYPE_PARAM) throw new TypeError('Unbound type parameter T' + s.ordinal + '.');
  if (v === null || v === undefined) {
    w.writeNil();
    return;
  }

  switch (s.kind) {
    case Kind.PRIM:
      writePrim(w, v, primOf(s));
      break;
    case Kind.BIN:
      w.writeBin(fromBase64(string(v)));
      break;
    case Kind.PRIM_ARRAY:
      writePrimArray(w, array(v), primOf(s));
      break;
    case Kind.SEQ: {
      const wire = boundWireForm(s);
      if (wire === Kind.PRIM_ARRAY) writePrimArray(w, array(v), primOf(s.args[0]));
      else if (wire === Kind.BIN) w.writeBin(fromBase64(string(v)));
      else writeArray(w, array(v), arg(s, 0), schemas);
      break;
    }
    case Kind.SET:
    case Kind.QUEUE:
    case Kind.STACK:
      writeArray(w, array(v), arg(s, 0), schemas);
      break;
    case Kind.MAP: {
      const entries = array(v);
      w.writeMapHeader(entries.length);
      for (const entry of entries) {
        const pair = array(entry);
        if (pair.length !== 2) throw new TypeError('A map entry must be [key, value].');
        write(w, pair[0], arg(s, 0), schemas);
        write(w, pair[1], arg(s, 1), schemas);
      }
      break;
    }
    case Kind.TUPLE: {
      const elements = array(v);
      if (elements.length !== s.args.length) {
        throw new TypeError('A tuple of ' + s.args.length + ' needs ' + s.args.length + ' elements.');
      }
      w.writeArrayHeader(elements.length);
      for (let i = 0; i < elements.length; i++) write(w, elements[i], s.args[i], schemas);
      break;
    }
    case Kind.NULLABLE:
      write(w, v, arg(s, 0), schemas);
      break;
    case Kind.DECL:
      writeDecl(w, v, schemas.find(declIdOf(s)), s.args, schemas);
      break;
    case Kind.EXTERNAL:
      w.writeRaw(fromBase64(string(v)));
      break;
    default:
      throw new TypeError('Unknown shape kind ' + s.kind + '.');
  }
}

/** @param {MsgPackWriter} w @param {readonly unknown[]} items @param {Type} element @param {Schemas} schemas */
function writeArray(w, items, element, schemas) {
  w.writeArrayHeader(items.length);
  for (const item of items) write(w, item, element, schemas);
}

/** @param {MsgPackWriter} w @param {unknown} v @param {Decl} d @param {readonly Type[]} typeArgs @param {Schemas} schemas */
function writeDecl(w, v, d, typeArgs, schemas) {
  if (d.declKind === 'object') {
    const members = array(v);
    w.writeArrayHeader(members.length);
    for (let i = 0; i < members.length; i++) {
      const m = i < d.members.length ? d.members[i] : null;
      if (m !== null) {
        write(w, members[i], bind(m, typeArgs), schemas);
      } else if (members[i] === null || members[i] === undefined) {
        w.writeNil();
      } else {
        throw new TypeError("'" + d.id + "' has no member with key " + i + '.');
      }
    }
  } else if (d.declKind === 'union') {
    const pair = array(v);
    if (pair.length !== 2) throw new TypeError('A union value must be [tag, value].');
    const tag = integerValue(pair[0]);
    const caseId = tag < 0n || tag > 0xffffn ? undefined : d.cases.get(Number(tag));
    if (caseId === undefined) throw new TypeError("Union '" + d.id + "' has no case " + tag + '.');
    w.writeArrayHeader(2);
    w.writeInt(Number(tag));
    write(w, pair[1], decl(caseId, ...typeArgs), schemas);
  } else {
    writePrim(w, v, d.underlying);
  }
}

/** @param {MsgPackWriter} w @param {unknown} v @param {string} p */
function writePrim(w, v, p) {
  switch (p) {
    case Prim.BOOL:
      if (typeof v !== 'boolean') throw new TypeError('Expected a JSON boolean, got ' + show(v) + '.');
      w.writeBool(v);
      break;
    case Prim.SBYTE:
      w.writeInt(Number(checkedRange(v, -128n, 127n)));
      break;
    case Prim.BYTE:
      w.writeInt(Number(checkedRange(v, 0n, 255n)));
      break;
    case Prim.INT16:
      w.writeInt(Number(checkedRange(v, -32768n, 32767n)));
      break;
    case Prim.UINT16:
    case Prim.CHAR:
      w.writeInt(Number(checkedRange(v, 0n, 65535n)));
      break;
    case Prim.INT32:
    case Prim.DATE_ONLY:
      w.writeInt(Number(checkedRange(v, -2147483648n, 2147483647n)));
      break;
    case Prim.UINT32:
      w.writeInt(Number(checkedRange(v, 0n, 4294967295n)));
      break;
    case Prim.INT64:
    case Prim.DATE_TIME:
    case Prim.TIME_SPAN:
    case Prim.TIME_ONLY:
      w.writeInt64(checkedRange(v, INT64_MIN, INT64_MAX));
      break;
    case Prim.UINT64:
      w.writeUInt64(checkedRange(v, 0n, UINT64_MAX));
      break;
    case Prim.SINGLE:
      writeSingleBits(w, parseSingle(v));
      break;
    case Prim.DOUBLE: {
      const bytes = new Uint8Array(9);
      bytes[0] = 0xcb;
      new DataView(bytes.buffer).setBigUint64(1, parseDouble(v));
      w.writeRaw(bytes);
      break;
    }
    case Prim.HALF:
      writeSingleBits(w, halfToSingleBits(parseHalf(v)));
      break;
    case Prim.STRING:
    case Prim.URI:
    case Prim.VERSION:
      w.writeString(string(v));
      break;
    case Prim.DATE_TIME_OFFSET: {
      const pair = array(v);
      if (pair.length !== 2) throw new TypeError('A DateTimeOffset must be [utcTicks, offsetMinutes].');
      w.writeArrayHeader(2);
      w.writeInt64(checkedRange(pair[0], INT64_MIN, INT64_MAX));
      w.writeInt(Number(checkedRange(pair[1], -32768n, 32767n)));
      break;
    }
    case Prim.DECIMAL: {
      const parts = array(v);
      if (parts.length !== 4) throw new TypeError('A decimal must be [lo, mid, hi, flags].');
      const bytes = new Uint8Array(16);
      const view = new DataView(bytes.buffer);
      for (let i = 0; i < 4; i++) view.setUint32(i * 4, Number(BigInt.asUintN(32, checkedRange(parts[i], -2147483648n, 4294967295n))), true);
      w.writeBin(bytes);
      break;
    }
    case Prim.GUID:
      w.writeBin(parseGuid(string(v)));
      break;
    case Prim.BIG_INTEGER: {
      const text = string(v);
      if (!/^-?[0-9]+$/.test(text)) throw new TypeError("'" + text + "' is not an integer.");
      w.writeBin(toTwosComplementLE(BigInt(text)));
      break;
    }
    default:
      throw new TypeError('Unknown primitive ' + p + '.');
  }
}

/** @param {MsgPackWriter} w @param {number} bits */
function writeSingleBits(w, bits) {
  const bytes = new Uint8Array(5);
  bytes[0] = 0xca;
  new DataView(bytes.buffer).setUint32(1, bits);
  w.writeRaw(bytes);
}

/** @param {MsgPackWriter} w @param {readonly unknown[]} items @param {string} element */
function writePrimArray(w, items, element) {
  const size = elementSize(element);
  const bytes = new Uint8Array(1 + items.length * size);
  const view = new DataView(bytes.buffer);
  bytes[0] = primitiveKind(element);
  for (let i = 0; i < items.length; i++) {
    const item = items[i];
    if (item === null || item === undefined) throw new TypeError('A primitive array cannot hold null.');
    const offset = 1 + i * size;
    switch (element) {
      case Prim.SBYTE:
        view.setInt8(offset, Number(checkedRange(item, -128n, 127n)));
        break;
      case Prim.INT16:
        view.setInt16(offset, Number(checkedRange(item, -32768n, 32767n)), true);
        break;
      case Prim.UINT16:
      case Prim.CHAR:
        view.setUint16(offset, Number(checkedRange(item, 0n, 65535n)), true);
        break;
      case Prim.INT32:
        view.setInt32(offset, Number(checkedRange(item, -2147483648n, 2147483647n)), true);
        break;
      case Prim.UINT32:
        view.setUint32(offset, Number(checkedRange(item, 0n, 4294967295n)), true);
        break;
      case Prim.INT64:
        view.setBigInt64(offset, checkedRange(item, INT64_MIN, INT64_MAX), true);
        break;
      case Prim.UINT64:
        view.setBigUint64(offset, checkedRange(item, 0n, UINT64_MAX), true);
        break;
      case Prim.SINGLE:
        view.setUint32(offset, parseSingle(item), true);
        break;
      case Prim.DOUBLE:
        view.setBigUint64(offset, parseDouble(item), true);
        break;
      case Prim.HALF:
        view.setUint16(offset, parseHalf(item), true);
        break;
      default:
        throw new TypeError(element + ' is not a primitive array element.');
    }
  }
  w.writeExtHeader(PRIMITIVE_ARRAY_EXT, bytes.length);
  w.writeRaw(bytes);
}

// ------------------------------------------------------------------ helpers

/**
 * The wire form of a sequence: a generic `T[]`/`List<T>`/`Memory<T>` bound to a primitive is an ext 78 primitive
 * array (bin for bytes), as the C# formatter of the closed type writes it.
 * @param {Type} s
 * @returns {string}
 */
function boundWireForm(s) {
  if (s.args.length !== 1 || s.args[0].kind !== Kind.PRIM) return Kind.SEQ;
  const element = primOf(s.args[0]);
  if (s.clr === 'Array' || s.clr === 'Memory' || s.clr === 'ReadOnlyMemory') {
    if (element === Prim.BYTE) return Kind.BIN;
    return isExtElement(element) ? Kind.PRIM_ARRAY : Kind.SEQ;
  }
  if (s.clr === 'List') return isExtElement(element) ? Kind.PRIM_ARRAY : Kind.SEQ;
  return Kind.SEQ;
}

const EXT_ELEMENTS = Object.freeze({
  SBYTE: [1, 1],
  INT16: [2, 2],
  UINT16: [3, 2],
  INT32: [4, 4],
  UINT32: [5, 4],
  INT64: [6, 8],
  UINT64: [7, 8],
  SINGLE: [8, 4],
  DOUBLE: [9, 8],
  CHAR: [10, 2],
  HALF: [11, 2],
});

/** @param {string} element @returns {boolean} */
function isExtElement(element) {
  return Object.prototype.hasOwnProperty.call(EXT_ELEMENTS, element);
}

/** @param {string} element @returns {number} */
function primitiveKind(element) {
  if (!isExtElement(element)) throw new TypeError(element + ' is not a primitive array element.');
  return /** @type {Record<string, number[]>} */ (EXT_ELEMENTS)[element][0];
}

/** @param {string} element @returns {number} */
function elementSize(element) {
  if (!isExtElement(element)) throw new TypeError(element + ' is not a primitive array element.');
  return /** @type {Record<string, number[]>} */ (EXT_ELEMENTS)[element][1];
}

/** @param {Type} type @returns {string} */
function primOf(type) {
  if (type.prim === null) throw new TypeError('The shape ' + describe(type) + ' has no primitive.');
  return type.prim;
}

/** @param {Type} type @returns {string} */
function declIdOf(type) {
  if (type.declId === null) throw new TypeError('The shape ' + describe(type) + ' has no declared type.');
  return type.declId;
}

/** @param {Type} s @param {number} index @returns {Type} */
function arg(s, index) {
  if (index >= s.args.length) throw new TypeError(s.kind + ' shape is missing argument ' + index + '.');
  return s.args[index];
}

/** @param {unknown} v @returns {string} */
function show(v) {
  try {
    return typeof v === 'bigint' ? v + 'n' : JSON.stringify(v) ?? String(v);
  } catch {
    return String(v);
  }
}

/** @param {unknown} v @returns {readonly unknown[]} */
function array(v) {
  if (Array.isArray(v)) return v;
  throw new TypeError('Expected a JSON array, got ' + show(v) + '.');
}

/** @param {unknown} v @returns {string} */
function string(v) {
  if (typeof v === 'string') return v;
  throw new TypeError('Expected a JSON string, got ' + show(v) + '.');
}

/**
 * A JSON integer: a number within ±(2^53 - 1), otherwise a decimal string.
 * @param {bigint | number} value
 * @returns {number | string}
 */
export function integer(value) {
  if (typeof value === 'number') return value;
  return value >= -9007199254740991n && value <= 9007199254740991n ? Number(value) : value.toString();
}

/**
 * The value of a typed-JSON integer: an integral number, a bigint or a decimal string.
 * @param {unknown} v
 * @returns {bigint}
 */
export function integerValue(v) {
  if (typeof v === 'bigint') return v;
  if (typeof v === 'number') {
    if (!Number.isInteger(v)) throw new TypeError('Expected an integer, got ' + show(v) + '.');
    return BigInt(v);
  }
  if (typeof v === 'string' && /^-?[0-9]+$/.test(v)) return BigInt(v);
  throw new TypeError('Expected an integer, got ' + show(v) + '.');
}

/** @param {unknown} v @param {bigint} min @param {bigint} max @returns {bigint} */
function checkedRange(v, min, max) {
  const value = integerValue(v);
  if (value < min || value > max) throw new RangeError(value + ' is outside [' + min + ', ' + max + '].');
  return value;
}

const scratch = new DataView(new ArrayBuffer(8));

/** @param {number} value @returns {number} */
function singleBitsOf(value) {
  scratch.setFloat32(0, value);
  return scratch.getUint32(0);
}

/** @param {number} bits @returns {number} */
function singleOfBits(bits) {
  scratch.setUint32(0, bits);
  return scratch.getFloat32(0);
}

/** @param {number} value @returns {bigint} */
function doubleBitsOf(value) {
  scratch.setFloat64(0, value);
  return scratch.getBigUint64(0);
}

/** @param {bigint} bits @returns {number} */
function doubleOfBits(bits) {
  scratch.setBigUint64(0, bits);
  return scratch.getFloat64(0);
}

/** @param {bigint | number} bits @param {number} digits @returns {string} */
function hexUpper(bits, digits) {
  return bits.toString(16).toUpperCase().padStart(digits, '0');
}

/** @param {number} bits @returns {string} */
function single(bits) {
  return 'f32:0x' + hexUpper(bits >>> 0, 8);
}

/** @param {bigint} bits @returns {string} */
function doubleBits(bits) {
  return 'f64:0x' + hexUpper(bits, 16);
}

/** @param {number} bits @returns {string} */
function half(bits) {
  return 'f16:0x' + hexUpper(bits & 0xffff, 4);
}

/**
 * The bits after a prefix (`f32:0x`…), or null when the value does not start with it.
 * @param {unknown} v @param {string} prefix @returns {bigint | null}
 */
function bitsOf(v, prefix) {
  if (typeof v !== 'string' || !v.startsWith(prefix)) return null;
  const digits = v.substring(prefix.length);
  if (!/^[0-9A-Fa-f]{1,16}$/.test(digits)) throw new TypeError("'" + v + "' is not a float bit pattern.");
  return BigInt('0x' + digits);
}

/** @param {unknown} v @returns {number} */
function number(v) {
  if (typeof v === 'number') return v;
  throw new TypeError('Expected a float bit pattern or a number, got ' + show(v) + '.');
}

/** The float64 bits of a typed-JSON float. @param {unknown} v @returns {bigint} */
function parseDouble(v) {
  let b = bitsOf(v, 'f64:0x');
  if (b !== null) return BigInt.asUintN(64, b);
  b = bitsOf(v, 'f32:0x');
  if (b !== null) return doubleBitsOf(singleOfBits(Number(BigInt.asUintN(32, b))));
  b = bitsOf(v, 'f16:0x');
  if (b !== null) return doubleBitsOf(singleOfBits(halfToSingleBits(Number(BigInt.asUintN(16, b)))));
  return doubleBitsOf(number(v));
}

/** The float32 bits of a typed-JSON float. @param {unknown} v @returns {number} */
function parseSingle(v) {
  let b = bitsOf(v, 'f32:0x');
  if (b !== null) return Number(BigInt.asUintN(32, b));
  b = bitsOf(v, 'f16:0x');
  if (b !== null) return halfToSingleBits(Number(BigInt.asUintN(16, b)));
  return singleBitsOf(doubleOfBits(parseDouble(v)));
}

/** The float16 bits of a typed-JSON float. @param {unknown} v @returns {number} */
function parseHalf(v) {
  const b = bitsOf(v, 'f16:0x');
  return b !== null ? Number(BigInt.asUintN(16, b)) : singleToHalfBits(parseSingle(v));
}

/** Widens float16 bits to float32 bits, exactly (NaN payloads included). @param {number} h @returns {number} */
function halfToSingleBits(h) {
  const sign = (h & 0x8000) << 16;
  const exp = (h >>> 10) & 0x1f;
  const mant = h & 0x3ff;
  if (exp === 0x1f) return (sign | 0x7f800000 | (mant << 13)) >>> 0;
  const magnitude = exp === 0 ? mant * 2 ** -24 : (1 + mant / 1024) * 2 ** (exp - 15);
  return (sign | singleBitsOf(magnitude)) >>> 0;
}

/** @param {number} x @returns {number} */
function roundHalfEven(x) {
  const f = Math.floor(x);
  const d = x - f;
  if (d > 0.5) return f + 1;
  if (d < 0.5) return f;
  return f % 2 === 0 ? f : f + 1;
}

/** Narrows float32 bits to float16 bits, rounding to nearest even as .NET does. @param {number} bits @returns {number} */
function singleToHalfBits(bits) {
  const sign = (bits >>> 16) & 0x8000;
  const exp = (bits >>> 23) & 0xff;
  const mant = bits & 0x7fffff;
  if (exp === 0xff) return mant !== 0 ? sign | 0x7e00 | (mant >>> 13) : sign | 0x7c00;
  const v = Math.abs(singleOfBits(bits & 0x7fffffff));
  if (v === 0) return sign;
  if (v < 2 ** -14) return sign | roundHalfEven(v / 2 ** -24);
  let e = Math.floor(Math.log2(v));
  while (2 ** e > v) e--;
  while (2 ** (e + 1) <= v) e++;
  let m = roundHalfEven((v / 2 ** e - 1) * 1024);
  if (m === 1024) {
    m = 0;
    e++;
  }
  if (e > 15) return sign | 0x7c00;
  return sign | ((e + 15) << 10) | m;
}

/** @param {Uint8Array} b @returns {string} */
function guid(b) {
  let s = '';
  for (let i = 0; i < 16; i++) {
    if (i === 4 || i === 6 || i === 8 || i === 10) s += '-';
    s += (b[i] < 16 ? '0' : '') + b[i].toString(16);
  }
  return s;
}

/** @param {string} text @returns {Uint8Array} */
function parseGuid(text) {
  const hex = text.replace(/-/g, '');
  if (!/^[0-9A-Fa-f]{32}$/.test(hex)) throw new TypeError("'" + text + "' is not a Guid.");
  const bytes = new Uint8Array(16);
  for (let i = 0; i < 16; i++) bytes[i] = parseInt(hex.substring(2 * i, 2 * i + 2), 16);
  return bytes;
}

/** Two's complement, little-endian, shortest form (as .NET `BigInteger.ToByteArray`). @param {bigint} n @returns {Uint8Array} */
function toTwosComplementLE(n) {
  const out = [];
  let x = n;
  for (;;) {
    const b = Number(BigInt.asUintN(8, x));
    out.push(b);
    x >>= 8n;
    if ((x === 0n && (b & 0x80) === 0) || (x === -1n && (b & 0x80) !== 0)) break;
  }
  return Uint8Array.from(out);
}

/** @param {Uint8Array} bytes @returns {bigint} */
function fromTwosComplementLE(bytes) {
  let n = 0n;
  for (let i = bytes.length - 1; i >= 0; i--) n = (n << 8n) | BigInt(bytes[i]);
  if ((bytes[bytes.length - 1] & 0x80) !== 0) n -= 1n << BigInt(8 * bytes.length);
  return n;
}

/**
 * Base64 (RFC 4648, padded) of bytes.
 * @param {Uint8Array} bytes
 * @returns {string}
 */
export function base64(bytes) {
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

/**
 * The bytes of base64 text (RFC 4648, padded); throws for anything else.
 * @param {string} text
 * @returns {Uint8Array}
 */
export function fromBase64(text) {
  if (text.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(text)) throw new TypeError("Invalid base64 '" + text + "'.");
  const binary = atob(text);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
  return out;
}
""";

    private const string TypedJsonDts = """
/** Typed JSON: the value model of the golden vectors and of the interop control protocol. */

export declare const MAX_SAFE_INTEGER: number;

export type KindName = 'PRIM' | 'BIN' | 'PRIM_ARRAY' | 'SEQ' | 'SET' | 'QUEUE' | 'STACK' | 'MAP' | 'TUPLE' | 'NULLABLE' | 'DECL' | 'TYPE_PARAM' | 'EXTERNAL';

export type PrimName = 'BOOL' | 'SBYTE' | 'BYTE' | 'INT16' | 'UINT16' | 'INT32' | 'UINT32' | 'INT64' | 'UINT64' | 'SINGLE' | 'DOUBLE'
  | 'HALF' | 'CHAR' | 'DECIMAL' | 'STRING' | 'DATE_TIME' | 'DATE_TIME_OFFSET' | 'TIME_SPAN' | 'DATE_ONLY' | 'TIME_ONLY' | 'GUID'
  | 'BIG_INTEGER' | 'URI' | 'VERSION';

/** The kinds of a type shape (the IR `TypeRefKind`). */
export declare const Kind: Readonly<{ [K in KindName]: K }>;

/** The built-in leaf types (the IR `PrimKind`). */
export declare const Prim: Readonly<{ [P in PrimName]: P }>;

/** The shape of a serialized type (the IR `TypeRef`). */
export interface Type {
  readonly kind: KindName;
  readonly prim: PrimName | null;
  readonly clr: string | null;
  readonly args: readonly Type[];
  readonly declId: string | null;
  readonly ordinal: number;
}

export declare function prim(p: PrimName): Type;
export declare function bin(): Type;
export declare function primArray(element: PrimName): Type;
export declare function seq(clr: string | null, element: Type): Type;
export declare function set(element: Type): Type;
export declare function queue(element: Type): Type;
export declare function stack(element: Type): Type;
export declare function map(key: Type, value: Type): Type;
export declare function tuple(clr: string, ...elements: Type[]): Type;
export declare function nullable(inner: Type): Type;
export declare function decl(id: string, ...typeArgs: Type[]): Type;
export declare function typeParam(ordinal: number): Type;
export declare function external(clr: string): Type;
export declare function bind(type: Type, typeArgs: readonly Type[]): Type;
export declare function describe(type: Type): string;

/** One keyed member of an object. */
export interface Member {
  readonly key: number;
  readonly type: Type;
}

/** A `[NexusObject]` class or struct: an array by key, nil in the gaps. */
export interface ObjectDecl {
  readonly declKind: 'object';
  readonly id: string;
  readonly length: number;
  readonly struct: boolean;
  readonly members: readonly (Type | null)[];
}

/** A union: `[tag, value]`. */
export interface UnionDecl {
  readonly declKind: 'union';
  readonly id: string;
  readonly cases: ReadonlyMap<number, string>;
}

/** An enum: its underlying integer. */
export interface EnumDecl {
  readonly declKind: 'enum';
  readonly id: string;
  readonly underlying: PrimName;
}

export type Decl = ObjectDecl | UnionDecl | EnumDecl;

export declare function member(key: number, type: Type): Member;
export declare function object(id: string, length: number, struct: boolean, ...members: Member[]): ObjectDecl;
export declare function union(id: string, ...cases: (number | string)[]): UnionDecl;
export declare function enumOf(id: string, underlying: PrimName): EnumDecl;

/** The declared types a shape may name. */
export declare class Schemas {
  constructor(...decls: Decl[]);
  /** The declaration of `id`; throws when there is none. */
  find(id: string): Decl;
}

/** Converts one MessagePack value to typed JSON. */
export declare function fromMsgPack(data: Uint8Array, type: Type, schemas: Schemas): unknown;
/** Converts typed JSON to one MessagePack value. */
export declare function toMsgPack(value: unknown, type: Type, schemas: Schemas): Uint8Array;
/** The typed JSON of the .NET default value of a shape. */
export declare function defaultValue(type: Type, schemas: Schemas): unknown;
/** A JSON integer: a number within ±(2^53 - 1), otherwise a decimal string. */
export declare function integer(value: bigint | number): number | string;
/** The value of a typed-JSON integer: an integral number, a bigint or a decimal string. */
export declare function integerValue(v: unknown): bigint;
/** Base64 (RFC 4648, padded) of bytes. */
export declare function base64(bytes: Uint8Array): string;
/** The bytes of base64 text (RFC 4648, padded). */
export declare function fromBase64(text: string): Uint8Array;
""";

    private const string ScaffoldingRuntimeJs = """
/**
 * The fixed part of the JavaScript test scaffolding (J11) the language host of the cross-language test harness builds
 * on (control protocol `docs/internals/interop-control-protocol.md`): typed-JSON codecs over the generated formatters,
 * the calls of a scriptable client nexus, and the base of the per-export scaffolding (invocation by method name).
 * @module
 */

import { MsgPackReader, MsgPackWriter, NexusClient, NexusDuplexChannel, NexusDuplexPipe } from '$RUNTIME$';
import { defaultValue, fromMsgPack, toMsgPack } from './typed-json.js';

const TRUSTED = Object.freeze({ preset: 'trusted' });

/** The slot of `onConnected`; its argument is `[isReconnected]`. */
export const ON_CONNECTED = 'OnConnected';

/** The slot of `onDisconnected`; it has no arguments. */
export const ON_DISCONNECTED = 'OnDisconnected';

/**
 * Typed JSON of one JavaScript type: the value goes through its generated formatter and typed JSON, so it has exactly
 * the wire form the C# side reads.
 * @template T
 */
export class TypedCodec {
  /**
   * @param {string} expression The golden type expression, declared types by full metadata name.
   * @param {import('$RUNTIME$').Formatter<T>} formatter The formatter of the JavaScript type.
   * @param {import('./typed-json.js').Type} type The shape.
   * @param {import('./typed-json.js').Schemas} schemas The declared types.
   */
  constructor(expression, formatter, type, schemas) {
    /** The golden type expression (`int`, `List<string>`, `NexNet.TestNexus.Interop.InteropItem`). @readonly */
    this.expression = expression;
    /** The formatter, for `client.createChannel` and the like. @readonly */
    this.formatter = formatter;
    /** The shape. @readonly */
    this.type = type;
    /** The declared types. @readonly */
    this.schemas = schemas;
    Object.freeze(this);
  }

  /**
   * The typed JSON of a value.
   * @param {T} value
   * @returns {unknown}
   */
  toJson(value) {
    const w = new MsgPackWriter();
    this.formatter.write(w, value);
    return fromMsgPack(w.toUint8Array().slice(), this.type, this.schemas);
  }

  /**
   * The value of typed JSON; null is the .NET default (zero, an all-default struct, null).
   * @param {unknown} json
   * @returns {T}
   */
  fromJson(json) {
    const value = json === null || json === undefined ? defaultValue(this.type, this.schemas) : json;
    const r = new MsgPackReader(toMsgPack(value, this.type, this.schemas), TRUSTED);
    const result = this.formatter.read(r);
    r.ensureEnd();
    return result;
  }

  /** @returns {string} */
  toString() {
    return this.expression;
  }
}

/** One call of a client nexus method (or lifecycle slot) that a {@link ScriptHost} runs. */
export class ScriptCall {
  /**
   * @param {string} slot The C# method name, {@link ON_CONNECTED} or {@link ON_DISCONNECTED}.
   * @param {readonly unknown[]} args The serialized arguments in wire order, typed JSON.
   * @param {AbortSignal | null} signal The call's cancellation; null when the method takes no token (the host then
   *   uses the client's lifetime).
   * @param {NexusDuplexPipe | NexusDuplexChannel<any> | null} pipe The pipe or channel argument, or null.
   * @param {TypedCodec<any> | null} channelItem The codec of the channel's items, or null.
   */
  constructor(slot, args, signal, pipe, channelItem) {
    /** @readonly */
    this.slot = slot;
    /** @readonly */
    this.args = Object.freeze([...args]);
    /** @readonly */
    this.signal = signal;
    /** @readonly */
    this.pipe = pipe;
    /** @readonly */
    this.channelItem = channelItem;
    Object.freeze(this);
  }

  /** @returns {string} */
  toString() {
    return this.slot + JSON.stringify(this.args, (_, v) => (typeof v === 'bigint' ? v.toString() : v));
  }
}

/**
 * The language host's side of a scriptable client nexus: it runs the script of each call (control protocol §6.6,
 * reference `ScriptInterpreter`): it reports the call (`script.call`), then runs the slot's steps.
 * @typedef {object} ScriptHost
 * @property {(call: ScriptCall) => unknown} call Runs the script of one call. Returns (or resolves to) the typed-JSON
 *   result; null or undefined is the .NET default result. A throw or rejection fails the call. Ignored for
 *   fire-and-forget methods and lifecycle slots.
 */

/**
 * Hands a call to the host; a host that throws gives a rejected promise.
 * @param {ScriptHost} host
 * @param {string} slot
 * @param {readonly unknown[]} args
 * @param {AbortSignal | null} signal
 * @param {NexusDuplexPipe | NexusDuplexChannel<any> | null} pipe
 * @param {TypedCodec<any> | null} channelItem
 * @returns {Promise<unknown>}
 */
export function runScript(host, slot, args, signal, pipe, channelItem) {
  try {
    return Promise.resolve(host.call(new ScriptCall(slot, args, signal, pipe, channelItem)));
  } catch (error) {
    return Promise.reject(error);
  }
}

/**
 * {@link runScript} for fire-and-forget methods and lifecycle slots: the result and errors are dropped.
 * @param {ScriptHost} host
 * @param {string} slot
 * @param {readonly unknown[]} args
 * @returns {void}
 */
export function runDetached(host, slot, args) {
  runScript(host, slot, args, null, null, null).catch(() => {});
}

/** One server method. */
export class Method {
  /**
   * @param {string} name The C# method name.
   * @param {number} id The wire id.
   * @param {'Void' | 'Task' | 'TaskOf'} returnKind How it completes (pipe and channel methods complete like `Task`).
   * @param {'None' | 'Pipe' | 'Channel'} pipeKind Its pipe parameter.
   * @param {boolean} cancellable True when it takes a cancellation token.
   * @param {readonly TypedCodec<any>[]} parameters The codecs of the serialized parameters, in wire order.
   * @param {TypedCodec<any> | null} result The codec of the result, or null.
   * @param {TypedCodec<any> | null} channelItem The codec of the channel items (create the channel with
   *   `client.createChannel(channelItem.formatter)`), or null.
   */
  constructor(name, id, returnKind, pipeKind, cancellable, parameters, result, channelItem) {
    /** @readonly */
    this.name = name;
    /** @readonly */
    this.id = id;
    /** @readonly */
    this.returnKind = returnKind;
    /** @readonly */
    this.pipeKind = pipeKind;
    /** @readonly */
    this.cancellable = cancellable;
    /** @readonly */
    this.parameters = Object.freeze([...parameters]);
    /** @readonly */
    this.result = result;
    /** @readonly */
    this.channelItem = channelItem;
    Object.freeze(this);
  }

  /** @returns {string} */
  toString() {
    return this.name + '#' + this.id;
  }
}

/** One synchronized collection. */
export class Collection {
  /**
   * @param {string} name The C# property name.
   * @param {number} id The wire id.
   * @param {number} mode The `NexusCollectionMode`.
   * @param {TypedCodec<any>} item The codec of the items.
   */
  constructor(name, id, mode, item) {
    /** @readonly */
    this.name = name;
    /** @readonly */
    this.id = id;
    /** @readonly */
    this.mode = mode;
    /** @readonly */
    this.item = item;
    Object.freeze(this);
  }

  /** @returns {string} */
  toString() {
    return this.name + '#' + this.id;
  }
}

/**
 * The test scaffolding of one exported nexus pair, for the language host: scriptable clients, invocation by method name
 * with typed-JSON arguments and results, and the codecs of its channels and collections. Generated code extends it.
 * @template {import('$RUNTIME$').ProxyBase} TProxy
 */
export class ScaffoldingExport {
  /**
   * @param {object} description
   * @param {string} description.clientNexus The full metadata name of the C# client nexus interface.
   * @param {string} description.serverNexus The full metadata name of the C# server nexus interface.
   * @param {import('$RUNTIME$').ClientBinding<TProxy>} description.binding The handshake and proxy class.
   * @param {readonly string[]} description.slots The client nexus slots.
   * @param {readonly Method[]} description.methods The server methods, by id.
   * @param {readonly Collection[]} description.collections The synchronized collections, by id.
   */
  constructor({ clientNexus, serverNexus, binding, slots, methods, collections }) {
    /** The full metadata name of the C# client nexus interface (`client.create`'s `nexus.client`). @readonly */
    this.clientNexus = clientNexus;
    /** The full metadata name of the C# server nexus interface (`client.create`'s `nexus.server`). @readonly */
    this.serverNexus = serverNexus;
    /** The handshake and proxy class, for `NexusClient.create` and pools. @readonly */
    this.binding = binding;
    /** The client nexus slots: the C# method names, then {@link ON_CONNECTED} and {@link ON_DISCONNECTED}. @readonly */
    this.slots = Object.freeze([...slots]);
    /** The server methods, by id. @readonly */
    this.methods = Object.freeze([...methods]);
    /** The synchronized collections, by id. @readonly */
    this.collections = Object.freeze([...collections]);
  }

  /** The greeting constants. @returns {import('$RUNTIME$').HandshakeDescriptor} */
  get handshake() {
    return this.binding.handshake;
  }

  /** The generated proxy class. @returns {new (session: any) => TProxy} */
  get proxyType() {
    return this.binding.proxy;
  }

  /**
   * A new scriptable client nexus. Generated.
   * @param {ScriptHost} host Runs the scripts of its calls.
   * @returns {import('$RUNTIME$').ClientNexusBase<TProxy>}
   */
  createNexus(host) {
    throw new Error('ScaffoldingExport.createNexus is generated.');
  }

  /**
   * Creates a client with a scriptable nexus.
   * @param {import('$RUNTIME$').ClientConfig} config
   * @param {ScriptHost} host Runs the scripts of its nexus.
   * @returns {NexusClient<any, TProxy>} The client, not connected.
   */
  createClient(config, host) {
    return NexusClient.create(config, this.createNexus(host), this.binding);
  }

  /**
   * Invokes a server method by name. The invocation is handed to the client runtime before this returns.
   * @param {TProxy} proxy The client's proxy.
   * @param {string} method The C# method name.
   * @param {readonly unknown[]} args The serialized arguments in wire order, typed JSON.
   * @param {{ pipe?: NexusDuplexPipe | NexusDuplexChannel<any> | null, signal?: AbortSignal }} [options] `pipe`: the
   *   pipe or channel of a pipe or channel method; `signal`: the call's cancellation (ignored by methods without a token).
   * @returns {Promise<unknown>} The typed-JSON result (null without one). A fire-and-forget method's promise resolves
   *   once it is handed to the runtime, a pipe or channel method's once the invocation is sent. Rejects with a
   *   `TypeError`/`RangeError` for an unknown method, a wrong argument count, an argument that does not fit its type,
   *   or a missing pipe.
   */
  invokeByName(proxy, method, args, options) {
    try {
      return Promise.resolve(this._invoke(proxy, method, args, options?.pipe ?? null, options?.signal));
    } catch (error) {
      return Promise.reject(error);
    }
  }

  /**
   * The switch over the server methods. Generated.
   * @protected
   * @param {TProxy} proxy
   * @param {string} method
   * @param {readonly unknown[]} args
   * @param {NexusDuplexPipe | NexusDuplexChannel<any> | null} pipe
   * @param {AbortSignal | undefined} signal
   * @returns {unknown}
   */
  _invoke(proxy, method, args, pipe, signal) {
    throw ScaffoldingExport.unknown('method', method, this);
  }

  /**
   * A synchronized collection by name.
   * @param {TProxy} proxy The client's proxy.
   * @param {string} collection The C# property name.
   * @returns {import('$RUNTIME$').NexusList<any>}
   */
  list(proxy, collection) {
    throw ScaffoldingExport.unknown('collection', collection, this);
  }

  /**
   * A method by name.
   * @param {string} name The C# method name.
   * @returns {Method | null}
   */
  method(name) {
    return this.methods.find((m) => m.name === name) ?? null;
  }

  /**
   * A collection by name.
   * @param {string} name The C# property name.
   * @returns {Collection | null}
   */
  collection(name) {
    return this.collections.find((c) => c.name === name) ?? null;
  }

  /** @returns {string} */
  toString() {
    return this.clientNexus + '/' + this.serverNexus;
  }

  /**
   * Checks the argument count.
   * @param {string} method
   * @param {readonly unknown[]} args
   * @param {number} count
   * @returns {void}
   */
  static expectArguments(method, args, count) {
    if (!Array.isArray(args)) throw new TypeError(method + ' takes an array of ' + count + ' arguments.');
    if (args.length !== count) throw new TypeError(method + ' takes ' + count + ' arguments, got ' + args.length + '.');
  }

  /**
   * The pipe argument.
   * @param {unknown} pipe
   * @returns {NexusDuplexPipe}
   */
  static pipe(pipe) {
    if (pipe instanceof NexusDuplexPipe) return pipe;
    throw new TypeError('The method takes a NexusDuplexPipe, got ' + String(pipe) + '.');
  }

  /**
   * The channel argument.
   * @param {unknown} pipe
   * @returns {NexusDuplexChannel<any>}
   */
  static channel(pipe) {
    if (pipe instanceof NexusDuplexChannel) return pipe;
    throw new TypeError('The method takes a NexusDuplexChannel, got ' + String(pipe) + '.');
  }

  /**
   * The typed JSON of a completion without a value.
   * @returns {null}
   */
  static nothing() {
    return null;
  }

  /**
   * An unknown method or collection.
   * @param {string} what
   * @param {string} name
   * @param {object} owner
   * @returns {TypeError}
   */
  static unknown(what, name, owner) {
    return new TypeError('Unknown ' + what + " '" + name + "' of " + String(owner) + '.');
  }
}
""";

    private const string ScaffoldingRuntimeDts = """
/** The fixed part of the JavaScript test scaffolding: typed-JSON codecs, script calls and the export base. */
import type { ClientBinding, ClientConfig, ClientNexusBase, Formatter, HandshakeDescriptor, NexusClient, NexusDuplexChannel, NexusDuplexPipe, NexusList, ProxyBase } from '$RUNTIME$';
import type { Schemas, Type } from './typed-json.js';

/** The slot of `onConnected`; its argument is `[isReconnected]`. */
export declare const ON_CONNECTED: 'OnConnected';

/** The slot of `onDisconnected`; it has no arguments. */
export declare const ON_DISCONNECTED: 'OnDisconnected';

/** Typed JSON of one JavaScript type, through its generated formatter. */
export declare class TypedCodec<T> {
  constructor(expression: string, formatter: Formatter<T>, type: Type, schemas: Schemas);
  /** The golden type expression, declared types by full metadata name. */
  readonly expression: string;
  /** The formatter, for `client.createChannel` and the like. */
  readonly formatter: Formatter<T>;
  /** The shape. */
  readonly type: Type;
  /** The declared types. */
  readonly schemas: Schemas;
  /** The typed JSON of a value. */
  toJson(value: T): unknown;
  /** The value of typed JSON; null is the .NET default. */
  fromJson(json: unknown): T;
  toString(): string;
}

/** One call of a client nexus method (or lifecycle slot) that a {@link ScriptHost} runs. */
export declare class ScriptCall {
  constructor(slot: string, args: readonly unknown[], signal: AbortSignal | null, pipe: NexusDuplexPipe | NexusDuplexChannel<any> | null, channelItem: TypedCodec<any> | null);
  /** The C# method name, {@link ON_CONNECTED} or {@link ON_DISCONNECTED}. */
  readonly slot: string;
  /** The serialized arguments in wire order, typed JSON. */
  readonly args: readonly unknown[];
  /** The call's cancellation; null when the method takes no token. */
  readonly signal: AbortSignal | null;
  /** The pipe or channel argument, or null. */
  readonly pipe: NexusDuplexPipe | NexusDuplexChannel<any> | null;
  /** The codec of the channel's items, or null. */
  readonly channelItem: TypedCodec<any> | null;
  toString(): string;
}

/** Runs the script of each call of a scriptable client nexus (control protocol §6.6). */
export interface ScriptHost {
  /** Returns (or resolves to) the typed-JSON result; null or undefined is the .NET default. A throw fails the call. */
  call(call: ScriptCall): unknown;
}

/** Hands a call to the host; a host that throws gives a rejected promise. */
export declare function runScript(host: ScriptHost, slot: string, args: readonly unknown[], signal: AbortSignal | null,
  pipe: NexusDuplexPipe | NexusDuplexChannel<any> | null, channelItem: TypedCodec<any> | null): Promise<unknown>;

/** {@link runScript} for fire-and-forget methods and lifecycle slots: the result and errors are dropped. */
export declare function runDetached(host: ScriptHost, slot: string, args: readonly unknown[]): void;

/** One server method. */
export declare class Method {
  constructor(name: string, id: number, returnKind: 'Void' | 'Task' | 'TaskOf', pipeKind: 'None' | 'Pipe' | 'Channel', cancellable: boolean,
    parameters: readonly TypedCodec<any>[], result: TypedCodec<any> | null, channelItem: TypedCodec<any> | null);
  readonly name: string;
  readonly id: number;
  readonly returnKind: 'Void' | 'Task' | 'TaskOf';
  readonly pipeKind: 'None' | 'Pipe' | 'Channel';
  readonly cancellable: boolean;
  readonly parameters: readonly TypedCodec<any>[];
  readonly result: TypedCodec<any> | null;
  readonly channelItem: TypedCodec<any> | null;
  toString(): string;
}

/** One synchronized collection. */
export declare class Collection {
  constructor(name: string, id: number, mode: number, item: TypedCodec<any>);
  readonly name: string;
  readonly id: number;
  readonly mode: number;
  readonly item: TypedCodec<any>;
  toString(): string;
}

/** The test scaffolding of one exported nexus pair. */
export declare class ScaffoldingExport<TProxy extends ProxyBase> {
  constructor(description: {
    clientNexus: string;
    serverNexus: string;
    binding: ClientBinding<TProxy>;
    slots: readonly string[];
    methods: readonly Method[];
    collections: readonly Collection[];
  });
  /** The full metadata name of the C# client nexus interface. */
  readonly clientNexus: string;
  /** The full metadata name of the C# server nexus interface. */
  readonly serverNexus: string;
  /** The handshake and proxy class. */
  readonly binding: ClientBinding<TProxy>;
  /** The client nexus slots: the C# method names, then `OnConnected` and `OnDisconnected`. */
  readonly slots: readonly string[];
  /** The server methods, by id. */
  readonly methods: readonly Method[];
  /** The synchronized collections, by id. */
  readonly collections: readonly Collection[];
  /** The greeting constants. */
  get handshake(): HandshakeDescriptor;
  /** The generated proxy class. */
  get proxyType(): new (session: any) => TProxy;
  /** A new scriptable client nexus. */
  createNexus(host: ScriptHost): ClientNexusBase<TProxy>;
  /** Creates a client with a scriptable nexus, not connected. */
  createClient(config: ClientConfig, host: ScriptHost): NexusClient<any, TProxy>;
  /** Invokes a server method by name with typed-JSON arguments; resolves to the typed-JSON result (null without one). */
  invokeByName(proxy: TProxy, method: string, args: readonly unknown[],
    options?: { pipe?: NexusDuplexPipe | NexusDuplexChannel<any> | null; signal?: AbortSignal }): Promise<unknown>;
  protected _invoke(proxy: TProxy, method: string, args: readonly unknown[], pipe: NexusDuplexPipe | NexusDuplexChannel<any> | null, signal: AbortSignal | undefined): unknown;
  /** A synchronized collection by name. */
  list(proxy: TProxy, collection: string): NexusList<any>;
  /** A method by name. */
  method(name: string): Method | null;
  /** A collection by name. */
  collection(name: string): Collection | null;
  toString(): string;
  static expectArguments(method: string, args: readonly unknown[], count: number): void;
  static pipe(pipe: unknown): NexusDuplexPipe;
  static channel(pipe: unknown): NexusDuplexChannel<any>;
  static nothing(): null;
  static unknown(what: string, name: string, owner: object): TypeError;
}
""";
}
