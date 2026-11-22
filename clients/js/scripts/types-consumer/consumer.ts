// Type-level check of the committed declarations (packages/nexnet/types), as a TypeScript consumer sees them. Compiled
// by scripts/check-types.js (npm run typecheck) with skipLibCheck off, so a declaration that re-exports a name its
// module does not declare fails here (J39: tsc dropped functions annotated with @type {TransportFactory}).
import { NexusClient as NodeEntryClient } from '@dtronix/nexnet/node';
import { NexusClient } from '@dtronix/nexnet';
import type { ClientConfig, Transport, TransportFactory, TransportKind } from '@dtronix/nexnet';
import type {
  httpSocketTransport,
  nodeTransports,
  nodeWebSocketTransport,
  tcpTlsTransport,
  tcpTransport,
  udsTransport,
} from '../../packages/nexnet/types/transport/node/index.js';
import type { webSocketTransport } from '../../packages/nexnet/types/transport/WebSocketTransport.js';

type IsAny<T> = 0 extends 1 & T ? true : false;
type Equals<A, B> = (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false;
type Assert<T extends true> = T;

/** A factory declaration: not `any` (a missing declaration), and exactly the TransportFactory signature. */
type IsFactory<F> = IsAny<F> extends true
  ? false
  : Equals<Parameters<F extends (...args: never[]) => unknown ? F : never>, Parameters<TransportFactory>> extends true
    ? Equals<ReturnType<F extends (...args: never[]) => unknown ? F : never>, Promise<Transport>>
    : false;

export type Checks = [
  Assert<IsFactory<typeof webSocketTransport>>,
  Assert<IsFactory<typeof nodeWebSocketTransport>>,
  Assert<IsFactory<typeof httpSocketTransport>>,
  Assert<IsFactory<typeof tcpTransport>>,
  Assert<IsFactory<typeof tcpTlsTransport>>,
  Assert<IsFactory<typeof udsTransport>>,
  // nodeTransports has a declared factory for every transport kind.
  Assert<Equals<keyof typeof nodeTransports, TransportKind>>,
  Assert<{ [K in TransportKind]: IsFactory<(typeof nodeTransports)[K]> }[TransportKind]>,
  Assert<Equals<Parameters<TransportFactory>, [ClientConfig, { signal: AbortSignal }]>>,
];

// The public entries: the client classes expose their transport tables as factories.
const nodeFactories: Partial<Record<TransportKind, TransportFactory>> = NodeEntryClient.transports;
const defaultFactories: Partial<Record<TransportKind, TransportFactory>> = NexusClient.transports;
export { nodeFactories, defaultFactories };
