/**
 * Internal helpers for the contract skeleton. Not exported from the package entry points.
 * @module
 */

/**
 * Creates the error thrown by a member that is not available (for example a missing transport or pipe layer).
 * Call sites use `throw notImplemented('Class.member')` so control-flow analysis sees the throw.
 *
 * @param {string} member Qualified member name, e.g. `'MsgPackWriter.writeInt32'`.
 * @returns {Error}
 */
export function notImplemented(member) {
  return new Error(`not implemented: ${member}`);
}
