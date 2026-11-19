/**
 * Transport contract between the session (J05) and the transports (J06).
 * Implemented in `src/transport/ITransport.js`; this file re-exports it.
 * @module
 */

export { unimplementedTransport } from '../transport/ITransport.js';

/** @typedef {import('../transport/ITransport.js').Transport} Transport */
/** @typedef {import('../transport/ITransport.js').TransportFactory} TransportFactory */
