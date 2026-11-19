/**
 * The browser language host: runs in the page the launcher (`src/node/launcher.js`) opens. It dials the launcher's
 * WebSocket bridge, which relays the control stream to the runner unchanged (control protocol §2), and runs the same
 * dispatcher as the Node host over the browser build of `@dtronix/nexnet` (WebSocket transports only).
 * @module
 */

import { Host, FrameDecoder } from '../../interop-host-node/src/core/index.js';

/** Capabilities of a browser client (§4.1): no Authorization header, client certificate, pinning or custom CA. */
export const BROWSER_CAPABILITIES = Object.freeze(['FakeTime', 'ClientPool']);

/** Transports of a browser client (D6). */
export const BROWSER_TRANSPORTS = Object.freeze(['WebSocket', 'WebSocketTls']);

/**
 * Starts the host. The launcher passes `bridge`, `token`, `lang`, `runtime` and `lib` in the page URL.
 * @param {import('../../interop-host-node/src/core/types.js').Generated} generated
 */
export function startPage(generated) {
  const params = new URLSearchParams(location.search);
  const bridge = params.get('bridge');
  const token = params.get('token');
  if (!bridge || !token) throw new Error('The page needs the bridge and token parameters.');

  const socket = new WebSocket(bridge);
  socket.binaryType = 'arraybuffer';
  const decoder = new FrameDecoder();
  const host = new Host(
    {
      lang: params.get('lang') ?? 'browser-chromium',
      token,
      runtime: params.get('runtime') ?? navigator.userAgent,
      lib: params.get('lib') ?? '@dtronix/nexnet',
      capabilities: BROWSER_CAPABILITIES,
      transports: BROWSER_TRANSPORTS,
      maxConcurrency: 4,
      browser: true,
      send: (frame) => socket.send(/** @type {Uint8Array<ArrayBuffer>} */ (frame)),
      close: () => socket.close(1000),
      exit: () => {
        socket.close(1000);
        /** @type {any} */ (globalThis).__nexnetHostExited = true;
      },
      dumpThreads: () => 'browser host (' + navigator.userAgent + '): a page has one thread; pending work below.',
    },
    generated,
  );

  socket.addEventListener('open', () => host.start());
  socket.addEventListener('message', (event) => {
    let messages;
    try {
      messages = decoder.push(new Uint8Array(/** @type {ArrayBuffer} */ (event.data)));
    } catch (error) {
      host.fail(error);
      return;
    }
    for (const message of messages) host.receive(message);
  });
  socket.addEventListener('close', () => host.closed());
  addEventListener('unhandledrejection', (event) => host.hostError(event.reason));
  addEventListener('error', (event) => host.hostError(event.error ?? event.message));
}
