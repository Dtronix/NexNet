/**
 * Collection modes.
 * @module
 */

/**
 * Synchronization mode of a collection: the `[NexusCollection(mode)]` of the server nexus property, which generated
 * code passes to `ProxyBase._collection`. Values match C# `NexusCollectionMode`. Only `BiDirectional` accepts
 * changes from a client; `Relay` behaves like `ServerToClient` for a client (protocol spec §12.2).
 */
export const NexusCollectionMode = Object.freeze({
  Unset: 0,
  ServerToClient: 1,
  BiDirectional: 2,
  Relay: 3,
});

/** @typedef {typeof NexusCollectionMode[keyof typeof NexusCollectionMode]} NexusCollectionModeValue */
