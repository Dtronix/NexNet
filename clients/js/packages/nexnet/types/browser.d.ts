/**
 * `@dtronix/nexnet` browser entry (the `browser` export condition). Transports: WebSocket only (D6).
 * Must never import `node:*`, directly or transitively (checked by test/entry-purity.test.js).
 * @module
 */
export * from './index.js';
