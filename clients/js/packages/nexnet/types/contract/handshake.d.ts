/**
 * What generated code hands the runtime about one export.
 * @module
 */
export type HandshakeDescriptor = {
    /**
     * Null when the server nexus is unversioned; otherwise at most 16 characters.
     */
    version: string | null;
    /**
     * Server nexus hash (int32).
     */
    serverNexusHash: number;
    /**
     * Client nexus hash (int32).
     */
    clientNexusHash: number;
    /**
     * Method IDs the client nexus accepts from the server.
     */
    acceptedIds: BigUint64Array | Set<number>;
};
export type ClientBinding<TProxy extends import('./proxy.js').ProxyBase> = {
    handshake: HandshakeDescriptor;
    /**
     * The generated proxy class.
     */
    proxy: new (session: import('./proxy.js').ProxySession) => TProxy;
};
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
