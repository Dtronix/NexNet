/**
 * Base class of generated server proxies. Implemented in `src/client/ProxyBase.js`; this file re-exports it.
 * @module
 */
export { ProxyBase } from '../client/ProxyBase.js';
export type ProxySession = import('../client/ProxyBase.js').ProxySession;
export type ArgumentsWriter = import('../client/ProxyBase.js').ArgumentsWriter;
export type PipeArgumentsWriter = import('../client/ProxyBase.js').PipeArgumentsWriter;
export type InvokeOptions<T> = import('../client/ProxyBase.js').InvokeOptions<T>;
/** @typedef {import('../client/ProxyBase.js').ProxySession} ProxySession */
/** @typedef {import('../client/ProxyBase.js').ArgumentsWriter} ArgumentsWriter */
/** @typedef {import('../client/ProxyBase.js').PipeArgumentsWriter} PipeArgumentsWriter */
/**
 * @template T
 * @typedef {import('../client/ProxyBase.js').InvokeOptions<T>} InvokeOptions
 */
