/**
 * The Node language host: dials the runner's control port, sends `hello` and runs the dispatcher
 * (docs/internals/interop-control-protocol.md).
 * @module
 */

import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import { register } from 'node:module';
import { pathToFileURL } from 'node:url';
import { Host, FrameDecoder } from '../core/index.js';
import { generatedDir, formattersDir, hostPackageDir, parseArgs, GENERATE_COMMAND } from './paths.js';

/** Capabilities of the Node client (control protocol §4.1): everything but QUIC; UDS where AF_UNIX is (not Windows). */
export function nodeCapabilities() {
  const capabilities = ['AuthorizationHeader', 'ClientCertificate', 'Pinning', 'CustomCa', 'RemoteCertificate', 'HttpSocket', 'Tcp', 'FakeTime', 'ClientPool'];
  // Node's UDS path on Windows is a named pipe, not the AF_UNIX socket the server listens on: a capability skip.
  if (process.platform !== 'win32') capabilities.push('Uds');
  return capabilities;
}

/** Transports of the Node client. */
export function nodeTransports() {
  const transports = ['Tcp', 'TcpTls', 'HttpSocket', 'HttpSocketTls', 'WebSocket', 'WebSocketTls'];
  if (process.platform !== 'win32') transports.splice(2, 0, 'Uds');
  return transports;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const control = args.control;
  const token = args.token;
  if (!control || !token) {
    process.stderr.write('Usage: node main.js --control 127.0.0.1:<port> --token <token>\n');
    process.exit(2);
  }

  const index = path.join(generatedDir, 'scaffolding', 'index.js');
  if (!fs.existsSync(index)) {
    process.stderr.write('The generated test scaffolding (' + index + ') is missing. Build it: ' + GENERATE_COMMAND + '\n');
    process.exit(3);
  }

  const directoryUrl = (/** @type {string} */ dir) => pathToFileURL(dir + path.sep).href;
  register('./loader.js', import.meta.url, {
    data: {
      generated: directoryUrl(generatedDir),
      formatters: directoryUrl(formattersDir),
      parent: pathToFileURL(path.join(hostPackageDir, 'src', 'main.js')).href,
    },
  });
  /** @type {any} */
  const scaffolding = await import(pathToFileURL(index).href);
  /** @type {any} */
  const codecs = await import(pathToFileURL(path.join(generatedDir, 'scaffolding', 'test-codecs.js')).href);
  const runtimePackage = JSON.parse(fs.readFileSync(path.join(hostPackageDir, '..', 'nexnet', 'package.json'), 'utf8'));

  const separator = control.lastIndexOf(':');
  const socket = net.connect({ host: control.slice(0, separator), port: Number(control.slice(separator + 1)) });
  socket.setNoDelay(true);
  const decoder = new FrameDecoder();
  let exiting = false;

  const host = new Host(
    {
      lang: 'node',
      token,
      runtime: 'node ' + process.versions.node,
      lib: '@dtronix/nexnet ' + runtimePackage.version,
      pid: process.pid,
      capabilities: nodeCapabilities(),
      transports: nodeTransports(),
      maxConcurrency: 8,
      browser: false,
      send: (frame) => {
        socket.write(frame);
      },
      close: () => {
        socket.end();
      },
      exit: (code) => {
        if (exiting) return;
        exiting = true;
        // Let the last frames (response, bye) leave before the process ends.
        const timer = setTimeout(() => process.exit(code), 2000);
        socket.once('close', () => {
          clearTimeout(timer);
          process.exit(code);
        });
        socket.end();
      },
      dumpThreads: () => {
        try {
          return JSON.stringify(process.report?.getReport?.() ?? {}, null, 1);
        } catch (error) {
          return 'process.report failed: ' + String(error);
        }
      },
    },
    { schemaHash: scaffolding.SCHEMA_HASH, find: scaffolding.find, forType: codecs.forType },
  );

  socket.on('connect', () => host.start());
  socket.on('data', (chunk) => {
    let messages;
    try {
      messages = decoder.push(new Uint8Array(chunk.buffer, chunk.byteOffset, chunk.byteLength));
    } catch (error) {
      host.fail(error);
      return;
    }
    for (const message of messages) host.receive(message);
  });
  socket.on('error', (error) => {
    process.stderr.write('Control connection error: ' + error.message + '\n');
  });
  socket.on('close', () => {
    if (exiting) return;
    if (decoder.midFrame) process.stderr.write('The control connection closed inside a frame.\n');
    host.closed();
    process.exit(decoder.midFrame ? 1 : 0);
  });

  process.on('unhandledRejection', (reason) => host.hostError(reason));
  process.on('uncaughtException', (error) => {
    process.stderr.write('Uncaught: ' + (error.stack ?? String(error)) + '\n');
    host.hostError(error);
  });
}

main().catch((error) => {
  process.stderr.write('The Node host failed to start: ' + (error?.stack ?? String(error)) + '\n');
  process.exit(1);
});
