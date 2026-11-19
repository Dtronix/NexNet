/**
 * Node-only additions. Reachable only from the `node` entry; must never be imported by the browser entry.
 * J06 implements the transports (`src/transport/node/`).
 * @module
 */

import { NexusClient } from './client.js';
import { nodeTransports } from '../transport/node/index.js';

/**
 * `NexusClient` with every Node transport. The Node entry exports it as `NexusClient` too.
 *
 * @template {import('./nexus.js').ClientNexusBase<TProxy>} TNexus
 * @template {import('./proxy.js').ProxyBase} TProxy
 * @extends {NexusClient<TNexus, TProxy>}
 */
export class NodeNexusClient extends NexusClient {
  /** @type {typeof NexusClient.transports} */
  static transports = nodeTransports;
}
