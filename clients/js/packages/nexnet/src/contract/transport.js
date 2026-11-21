/**
 * Transport contract between the session and the transports.
 * Implemented in `src/transport/ITransport.js`; this file re-exports it.
 * @module
 */

export { unimplementedTransport } from '../transport/ITransport.js';

/** @typedef {import('../transport/ITransport.js').Transport} Transport */
/** @typedef {import('../transport/ITransport.js').TransportFactory} TransportFactory */
