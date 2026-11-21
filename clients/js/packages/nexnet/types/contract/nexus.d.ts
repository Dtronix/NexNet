/**
 * Base class of generated client nexuses. Implemented in `src/client/ClientNexusBase.js`; this file
 * re-exports it.
 * @module
 */
export { ClientNexusBase } from '../client/ClientNexusBase.js';
export type ClientContext<TProxy extends ProxyBase> = import('../client/ClientNexusBase.js').ClientContext<TProxy>;
export type ProxyBase = import('../client/ProxyBase.js').ProxyBase;
export type DispatchContext = import('../client/ClientNexusBase.js').DispatchContext;
export type NexusRemoteError = import('../client/ClientNexusBase.js').NexusRemoteError;
/**
 * @template {ProxyBase} TProxy
 * @typedef {import('../client/ClientNexusBase.js').ClientContext<TProxy>} ClientContext
 */
/** @typedef {import('../client/ProxyBase.js').ProxyBase} ProxyBase */
/** @typedef {import('../client/ClientNexusBase.js').DispatchContext} DispatchContext */
/** @typedef {import('../client/ClientNexusBase.js').NexusRemoteError} NexusRemoteError */
