/// <reference types="node" />
/**
 * HttpSocket transport: an HTTP/1.1 connection upgraded to `nexnet-httpsockets` (protocol spec §13.6). Node only.
 * @module
 */

import { toUrl, transportError } from '../TransportBase.js';
import { SocketTransport } from './SocketTransport.js';
import { headerHasToken, httpUpgrade } from './upgrade.js';

/** The HttpSocket upgrade token. */
export const HTTP_SOCKET_UPGRADE = 'nexnet-httpsockets';

/**
 * Opens an HttpSocket transport (`ClientConfig.httpSocket`). Bytes that arrive with the `101` are fed to the session
 * first. `401`/`403` reject with `transportError` `AuthenticationError`; a `3xx` redirect is `ProtocolError` and is never
 * followed; any other status, `2xx` included, is a connect failure (`InternalError`). `status` is set on each.
 *
 * @type {import('../ITransport.js').TransportFactory}
 */
export async function httpSocketTransport(config, { signal }) {
  const options = /** @type {import('../../contract/config.js').HttpSocketTransportConfig} */ (config.httpSocket);
  const url = toUrl(options.url);
  const secure = url.protocol === 'https:';
  const { response, socket, head } = await httpUpgrade({
    transport: 'HttpSocket',
    url,
    secure,
    headers: { ...options.headers, Connection: 'Upgrade', Upgrade: HTTP_SOCKET_UPGRADE },
    queryToken: options.queryToken,
    tls: options.tls,
    signal,
  });
  if (!headerHasToken(response.headers.upgrade, HTTP_SOCKET_UPGRADE)) {
    socket.destroy();
    throw transportError(`HttpSocket upgrade to ${url.href} answered with Upgrade '${response.headers.upgrade ?? ''}'.`, { kind: 'ProtocolError' });
  }
  return new SocketTransport(socket, { name: `${url.protocol}//`, isSecure: secure, head });
}
