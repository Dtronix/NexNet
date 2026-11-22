/**
 * What generated code hands the runtime about one export.
 * @module
 */

/**
 * Handshake constants of one export, copied from the generator IR (`ExportIr.Handshake`). Never recompute them.
 *
 * @typedef {object} HandshakeDescriptor
 * @property {string | null} version Null when the server nexus is unversioned; otherwise at most 16 characters.
 * @property {number} serverNexusHash Server nexus hash (int32).
 * @property {number} clientNexusHash Client nexus hash (int32).
 * @property {BigUint64Array | Set<number>} acceptedIds Method IDs the client nexus accepts from the server.
 */

/**
 * Binds a client nexus to its server proxy. Generated `create<Export>Client(config, nexus)` passes it to
 * {@link NexusClient.create}.
 *
 * @template {import('./proxy.js').ProxyBase} TProxy
 * @typedef {object} ClientBinding
 * @property {HandshakeDescriptor} handshake
 * @property {new (session: import('./proxy.js').ProxySession) => TProxy} proxy The generated proxy class.
 */

export {};
