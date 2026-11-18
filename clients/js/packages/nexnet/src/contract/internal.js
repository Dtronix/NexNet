/**
 * Internal helpers for the contract skeleton. Not exported from the package entry points.
 * @module
 */

/**
 * Creates the error every skeleton body throws until its implementation card lands.
 * Call sites use `throw notImplemented('Class.member')` so control-flow analysis sees the throw.
 *
 * @param {string} member Qualified member name, e.g. `'MsgPackWriter.writeInt32'`.
 * @returns {Error}
 */
export function notImplemented(member) {
  return new Error(`not implemented: ${member}`);
}
