/**
 * Transport contract between the session and the transports.
 * Implemented in `src/transport/ITransport.js`; this file re-exports it.
 * @module
 */
export { unimplementedTransport } from '../transport/ITransport.js';
export type Transport = import('../transport/ITransport.js').Transport;
export type TransportFactory = import('../transport/ITransport.js').TransportFactory;
/** @typedef {import('../transport/ITransport.js').Transport} Transport */
/** @typedef {import('../transport/ITransport.js').TransportFactory} TransportFactory */
