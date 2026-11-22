/**
 * C# enum names of the runtime's numeric enums, as the control protocol carries them.
 * @module
 */

import { ConnectionState, ConnectionResultState, DisconnectReason, RemoteExceptionDetail, NexusCollectionMode, NexusCollectionState } from '@dtronix/nexnet';

/**
 * @param {Readonly<Record<string, number>>} values
 * @returns {Map<number, string>}
 */
function invert(values) {
  return new Map(Object.entries(values).map(([name, value]) => [value, name]));
}

const stateNames = invert(ConnectionState);
const reasonNames = invert(DisconnectReason);
const resultNames = invert(ConnectionResultState);
const collectionModeNames = invert(NexusCollectionMode);
const collectionStateNames = invert(NexusCollectionState);

/** @param {number} state @returns {string} */
export const stateName = (state) => stateNames.get(state) ?? 'Unset';

/** @param {number | null | undefined} reason @returns {string} */
export const reasonName = (reason) => reasonNames.get(reason ?? 0) ?? 'None';

/** @param {number} state @returns {string} */
export const resultStateName = (state) => resultNames.get(state) ?? 'Unset';

/** @param {number} mode A `NexusCollectionMode` value. @returns {string} */
export const collectionModeName = (mode) => collectionModeNames.get(mode) ?? 'Unset';

/** @param {number} state A `NexusCollectionState` value. @returns {string} */
export const collectionStateName = (state) => collectionStateNames.get(state) ?? 'Unset';

/**
 * @param {string} name
 * @returns {number | undefined}
 */
export function stateValue(name) {
  return Object.prototype.hasOwnProperty.call(ConnectionState, name) ? /** @type {any} */ (ConnectionState)[name] : undefined;
}

/**
 * @param {string} name A `DisconnectReason` name.
 * @returns {number | undefined}
 */
export function reasonValue(name) {
  return Object.prototype.hasOwnProperty.call(DisconnectReason, name) ? /** @type {any} */ (DisconnectReason)[name] : undefined;
}

/**
 * @param {string} name
 * @returns {number | undefined}
 */
export function remoteExceptionDetailValue(name) {
  return Object.prototype.hasOwnProperty.call(RemoteExceptionDetail, name) ? /** @type {any} */ (RemoteExceptionDetail)[name] : undefined;
}

/** The runtime's log levels by C# `NexusLogLevel` name; `None` disables. */
export const LOG_LEVELS = Object.freeze({
  Trace: 'trace',
  Debug: 'debug',
  Information: 'information',
  Warning: 'warning',
  Error: 'error',
  Critical: 'critical',
  None: 'none',
});

/** @type {Readonly<Record<string, number>>} */
const LEVEL_ORDER = Object.freeze({ trace: 0, debug: 1, information: 2, warning: 3, error: 4, critical: 5, none: 6 });

/** @type {Readonly<Record<string, string>>} */
const LEVEL_NAMES = Object.freeze({
  trace: 'Trace',
  debug: 'Debug',
  information: 'Information',
  warning: 'Warning',
  error: 'Error',
  critical: 'Critical',
});

/** @param {string} level A runtime level. @returns {number} */
export const levelOrder = (level) => LEVEL_ORDER[level] ?? 6;

/** @param {string} level A runtime level. @returns {string} The C# name. */
export const levelName = (level) => LEVEL_NAMES[level] ?? 'Information';

/** @param {unknown} name A C# name. @returns {string | undefined} The runtime level. */
export function runtimeLevel(name) {
  return typeof name === 'string' && Object.prototype.hasOwnProperty.call(LOG_LEVELS, name)
    ? /** @type {any} */ (LOG_LEVELS)[name]
    : undefined;
}
