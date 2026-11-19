/**
 * The JavaScript formatter of the zoo's user-formatter type `InteropPoint` (`[assembly: NexusTargetFormatter<InteropPoint>(
 * NexusTargets.JavaScript, "./interop-point-formatter.js#InteropPointFormatter")]` in `src/NexNet.TestNexus`): the C#
 * `InteropPointFormatter` writes `[X, Y]`, two canonical integers. The generated code imports it from its own
 * directory; the hosts resolve that import here (`src/node/loader.js`, the browser launcher's bundler).
 * @module
 */

/** @type {import('@dtronix/nexnet').Formatter<[number, number]>} */
export const InteropPointFormatter = Object.freeze({
  write(writer, value) {
    if (!Array.isArray(value) || value.length !== 2) throw new TypeError('An InteropPoint is [x, y].');
    writer.writeArrayHeader(2);
    writer.writeInt(value[0]);
    writer.writeInt(value[1]);
  },
  read(reader) {
    reader.enter();
    reader.readArrayHeader(2);
    const x = reader.readInt32();
    const y = reader.readInt32();
    reader.exit();
    return /** @type {[number, number]} */ ([x, y]);
  },
});
