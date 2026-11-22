/**
 * `@dtronix/nexnet` browser entry (the `browser` export condition). Transports: WebSocket only (browsers cannot open raw sockets or take over an HTTP connection).
 * Must never import `node:*`, directly or transitively (checked by test/entry-purity.test.js).
 * @module
 */
export * from './index.js';
