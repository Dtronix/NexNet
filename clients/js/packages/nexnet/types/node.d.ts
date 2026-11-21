/**
 * `@dtronix/nexnet` Node entry (the `node` condition of `.`, and `./node`).
 * Everything in the platform-neutral entry, plus HttpSocket, TCP, TLS and UDS transports.
 * Its `NexusClient` is {@link NodeNexusClient}, which knows every Node transport.
 * @module
 */
export * from './index.js';
export { NodeNexusClient, NodeNexusClient as NexusClient, NodeNexusClientPool, NodeNexusClientPool as NexusClientPool, } from './contract/node.js';
