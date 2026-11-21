/**
 * The collection-sync channel items (protocol spec §4.11): a union value `[tag: uint16, body: array]` whose body starts
 * with `flags: uint8`. Mirrors C# `NexusCollectionListMessageFormatter` and the `NexusCollectionList*Message` classes.
 * @module
 */

import { NexusError } from '../contract/errors.js';

/** Union tags of the list items (§4.11). */
export const ListMessageTag = Object.freeze({
  ResetStart: 0,
  ResetValues: 1,
  ResetComplete: 2,
  Clear: 3,
  Insert: 4,
  Replace: 5,
  Move: 6,
  Remove: 7,
  Noop: 8,
});

/** @typedef {typeof ListMessageTag[keyof typeof ListMessageTag]} ListMessageTagValue */

/** Item flags (§12.3). Bit 0 is Ack; the other bits are reserved. */
export const CollectionMessageFlags = Object.freeze({
  None: 0,
  Ack: 1,
});

/**
 * One decoded list item. Only the fields of its tag are set.
 *
 * @template T
 * @typedef {object} ListMessage
 * @property {ListMessageTagValue} tag
 * @property {number} flags
 * @property {number} [version] ResetStart: the snapshot version. Server change: the version after the change.
 *   Client request: the base version.
 * @property {number} [totalValues] ResetStart.
 * @property {T[] | null} [values] ResetValues (null when the server wrote nil).
 * @property {number} [index] Insert, Replace, Remove.
 * @property {T} [value] Insert, Replace.
 * @property {number} [fromIndex] Move.
 * @property {number} [toIndex] Move.
 */

/** Body lengths by tag, flags included. */
const BODY_LENGTH = [3, 2, 1, 2, 4, 4, 4, 3, 1];

/**
 * @param {import('../contract/codec.js').MsgPackReader} reader
 * @param {number} min
 * @param {number} max
 * @param {string} name
 * @returns {number}
 */
function readRanged(reader, min, max, name) {
  const value = reader.readInt();
  if (value < min || value > max) throw new NexusError(`Collection item field ${name} is out of range: ${value}.`);
  return value;
}

/** @param {import('../contract/codec.js').MsgPackReader} reader @param {string} name */
const readInt32 = (reader, name) => readRanged(reader, -0x80000000, 0x7fffffff, name);

/**
 * The formatter of a C# `T[]` written as a MessagePack array of items, or nil for null. C# writes arrays of most
 * primitive numeric types as ext 78 instead (protocol spec §10.5); generated code passes its own `T[]` formatter for
 * those.
 *
 * @template T
 * @param {import('../contract/codec.js').Formatter<T>} itemFormatter
 * @returns {import('../contract/codec.js').Formatter<T[] | null>}
 */
export function arrayFormatter(itemFormatter) {
  return Object.freeze({
    /**
     * @param {import('../contract/codec.js').MsgPackWriter} writer
     * @param {T[] | null} values
     */
    write(writer, values) {
      if (values === null || values === undefined) {
        writer.writeNil();
        return;
      }
      writer.writeArrayHeader(values.length);
      for (const value of values) itemFormatter.write(writer, value);
    },
    /**
     * @param {import('../contract/codec.js').MsgPackReader} reader
     * @returns {T[] | null}
     */
    read(reader) {
      if (reader.tryReadNil()) return null;
      const count = reader.readArrayHeader();
      /** @type {T[]} */
      const values = [];
      for (let i = 0; i < count; i++) values.push(itemFormatter.read(reader));
      return values;
    },
  });
}

/**
 * The channel formatter of the list items for one item type.
 *
 * @template T
 * @param {import('../contract/codec.js').Formatter<T>} itemFormatter
 * @param {import('../contract/codec.js').Formatter<T[] | null>} [valuesFormatter] The C# `T[]` formatter, for the
 *   values of ResetValues. Default {@link arrayFormatter}.
 * @returns {import('../contract/codec.js').Formatter<ListMessage<T>>}
 */
export function listMessageFormatter(itemFormatter, valuesFormatter = arrayFormatter(itemFormatter)) {
  return Object.freeze({
    /**
     * @param {import('../contract/codec.js').MsgPackWriter} writer
     * @param {ListMessage<T>} message
     */
    write(writer, message) {
      const tag = message.tag;
      const length = BODY_LENGTH[tag];
      if (length === undefined) throw new RangeError(`Unknown collection item tag ${tag}.`);
      writer.writeArrayHeader(2);
      writer.writeInt(tag);
      writer.writeArrayHeader(length);
      writer.writeInt(message.flags ?? 0);
      switch (tag) {
        case ListMessageTag.ResetStart:
          writer.writeInt(/** @type {number} */ (message.version));
          writer.writeInt(/** @type {number} */ (message.totalValues));
          break;
        case ListMessageTag.ResetValues:
          valuesFormatter.write(writer, message.values ?? null);
          break;
        case ListMessageTag.Clear:
          writer.writeInt(/** @type {number} */ (message.version));
          break;
        case ListMessageTag.Insert:
        case ListMessageTag.Replace:
          writer.writeInt(/** @type {number} */ (message.version));
          writer.writeInt(/** @type {number} */ (message.index));
          itemFormatter.write(writer, /** @type {T} */ (message.value));
          break;
        case ListMessageTag.Move:
          writer.writeInt(/** @type {number} */ (message.version));
          writer.writeInt(/** @type {number} */ (message.fromIndex));
          writer.writeInt(/** @type {number} */ (message.toIndex));
          break;
        case ListMessageTag.Remove:
          writer.writeInt(/** @type {number} */ (message.version));
          writer.writeInt(/** @type {number} */ (message.index));
          break;
        default:
          // ResetComplete and Noop carry only the flags.
          break;
      }
    },

    /**
     * @param {import('../contract/codec.js').MsgPackReader} reader
     * @returns {ListMessage<T>}
     */
    read(reader) {
      if (reader.readArrayHeader() !== 2) throw new NexusError('A collection item is not a [tag, body] union value.');
      const tag = /** @type {ListMessageTagValue} */ (readRanged(reader, 0, 0xffff, 'tag'));
      const length = BODY_LENGTH[tag];
      if (length === undefined) throw new NexusError(`Unknown collection item tag ${tag}.`);
      const actual = reader.readArrayHeader();
      if (actual !== length) throw new NexusError(`Collection item ${tag} has ${actual} fields; expected ${length}.`);
      const flags = readRanged(reader, 0, 0xff, 'flags');
      switch (tag) {
        case ListMessageTag.ResetStart:
          return { tag, flags, version: readInt32(reader, 'version'), totalValues: readInt32(reader, 'totalValues') };
        case ListMessageTag.ResetValues:
          return { tag, flags, values: valuesFormatter.read(reader) };
        case ListMessageTag.Clear:
          return { tag, flags, version: readInt32(reader, 'version') };
        case ListMessageTag.Insert:
        case ListMessageTag.Replace: {
          const version = readInt32(reader, 'version');
          const index = readInt32(reader, 'index');
          return { tag, flags, version, index, value: itemFormatter.read(reader) };
        }
        case ListMessageTag.Move: {
          const version = readInt32(reader, 'version');
          const fromIndex = readInt32(reader, 'fromIndex');
          return { tag, flags, version, fromIndex, toIndex: readInt32(reader, 'toIndex') };
        }
        case ListMessageTag.Remove: {
          const version = readInt32(reader, 'version');
          return { tag, flags, version, index: readInt32(reader, 'index') };
        }
        default:
          return { tag, flags };
      }
    },
  });
}
