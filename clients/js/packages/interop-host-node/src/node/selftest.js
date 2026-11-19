/**
 * `npm run selftest:host`: a minimal runner (control protocol §2, §4) that launches a JavaScript language host from its
 * `host.json`, checks its `hello` (token, protocol, schema hash) and the warm-up, and, given the address of an interop
 * server (`src/NexNet.InteropServer`, H07) that hosts `IInteropServerNexus`, connects a client, invokes `EchoString`
 * and echoes a pipe through `EchoPipe`.
 *
 *   npm run selftest:host -- [--lang node|browser-chromium] [--transport WebSocket --address ws://127.0.0.1:5000/interop]
 *     [--ca path/to/test-ca.pem]
 * @module
 */

import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { FrameDecoder, encodeFrame, pattern, Sha256 } from '../core/index.js';
import { repoRoot, parseArgs } from './paths.js';

const args = parseArgs(process.argv.slice(2));
const lang = args.lang ?? 'node';

/** @param {string} message */
function fail(message) {
  process.stderr.write('selftest: FAILED: ' + message + '\n');
  process.exit(1);
}

/** @param {string} message */
function step(message) {
  process.stdout.write('selftest: ' + message + '\n');
}

const hostJson = [path.join(repoRoot, 'artifacts', 'interop', lang, 'host.json'), path.join(repoRoot, 'artifacts', 'interop', 'browser', 'host.json')]
  .find((p) => fs.existsSync(p) && (p.includes(lang) || lang.startsWith('browser-')));
if (!hostJson) fail('no host.json for ' + lang + '; run npm run build:interop-host --prefix clients/js');
const config = JSON.parse(fs.readFileSync(/** @type {string} */ (hostJson), 'utf8'));

const token = crypto.randomBytes(32).toString('hex');
const server = net.createServer();
await new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(undefined)));
const port = /** @type {net.AddressInfo} */ (server.address()).port;
const hostArgs = [...config.args, '--control', '127.0.0.1:' + port, '--token', token];
if (lang.startsWith('browser-')) hostArgs.push('--browser', lang.slice('browser-'.length));
const child = spawn(config.command, hostArgs, {
  cwd: path.resolve(repoRoot, config.workingDir ?? '.'),
  env: { ...process.env, ...(config.env ?? {}) },
  stdio: ['ignore', 'inherit', 'inherit'],
});
child.on('exit', (code) => step('host exited with code ' + code));

/** @type {net.Socket} */
const socket = await new Promise((resolve) => server.once('connection', resolve));
const decoder = new FrameDecoder();
/** @type {Array<Record<string, any>>} */
const inbox = [];
/** @type {Array<() => void>} */
const wakers = [];
socket.on('data', (chunk) => {
  for (const message of decoder.push(new Uint8Array(chunk))) inbox.push(message);
  for (const wake of wakers.splice(0)) wake();
});

/**
 * @param {(m: Record<string, any>) => boolean} match
 * @param {number} [timeoutMs]
 * @returns {Promise<Record<string, any>>}
 */
async function next(match, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const index = inbox.findIndex(match);
    if (index >= 0) return inbox.splice(index, 1)[0];
    if (Date.now() > deadline) fail('timed out waiting for a control message');
    await new Promise((resolve) => {
      wakers.push(() => resolve(undefined));
      setTimeout(resolve, 100);
    });
  }
}

let nextId = 1;
/**
 * @param {string | null} test
 * @param {string} op
 * @param {Record<string, unknown>} [opArgs]
 * @returns {Promise<Record<string, any>>}
 */
async function request(test, op, opArgs = {}) {
  const id = nextId++;
  socket.write(encodeFrame({ t: 'req', id, test, op, args: opArgs, timeoutMs: 30000 }));
  const res = await next((m) => m.t === 'res' && m.id === id);
  if (!res.ok) throw new Error(op + ' failed: ' + JSON.stringify(res.error));
  return res.value ?? {};
}

const hello = await next((m) => m.t === 'hello');
if (hello.token !== token) fail('hello carries the wrong token');
if (hello.proto !== 1) fail('hello has protocol ' + hello.proto);
const irPath = path.join(repoRoot, 'src', 'NexNet.TestNexus', 'obj', 'nexnet-ir.json');
if (fs.existsSync(irPath)) {
  const expected = crypto.createHash('sha256').update(fs.readFileSync(irPath)).digest('hex');
  if (hello.schemaHash !== expected) fail('schema hash ' + hello.schemaHash + ' is not the current IR ' + expected);
}
step('hello ' + hello.lang + ' (' + hello.runtime + ', ' + hello.lib + '), transports ' + hello.transports.join(','));

await request(null, 'ping');
await request('__warmup', 'test.begin');
const warmup = await request('__warmup', 'test.end');
if (warmup.openClients + warmup.openPipes + warmup.timers + warmup.pendingInvocations + warmup.subscriptions !== 0) fail('warm-up leaked');
step('warm-up clean');

if (args.address) {
  const test = 'selftest';
  await request(test, 'test.begin');
  /** @type {Record<string, unknown>} */
  const spec = { transport: args.transport ?? 'WebSocket', address: args.address };
  if (args.ca) spec.caPem = fs.readFileSync(args.ca, 'utf8');
  const nexus = { client: 'NexNet.TestNexus.Interop.IInteropClientNexus', server: 'NexNet.TestNexus.Interop.IInteropServerNexus' };
  const { h } = await request(test, 'client.create', { spec, nexus });
  await request(test, 'client.connect', { h });
  step('connected over ' + spec.transport);
  const method = (/** @type {string} */ name) => ({ nexus: nexus.server, name, id: 0 });
  const echo = await request(test, 'invoke', { h, method: method('EchoString'), args: ['hi'] });
  if (echo.result !== 'hi') fail('EchoString returned ' + JSON.stringify(echo.result));
  step('invoke EchoString ok');
  const { pipe } = await request(test, 'pipe.create', { h });
  const { future } = await request(test, 'invoke', { h, method: method('EchoPipe'), args: [], pipe, detach: true });
  await request(test, 'pipe.ready', { pipe });
  const length = 300000;
  const drain = request(test, 'pipe.drain', { pipe });
  await request(test, 'pipe.write', { pipe, pattern: { seed: 7, length } });
  await request(test, 'pipe.complete', { pipe });
  const digest = await drain;
  const expected = new Sha256().update(pattern(0, length, 7)).hex();
  if (digest.length !== length || digest.sha256 !== expected) fail('pipe echo returned ' + JSON.stringify(digest));
  await request(test, 'future.await', { future });
  await request(test, 'pipe.dispose', { pipe });
  step('pipe echo ok (' + length + ' bytes)');
  await request(test, 'client.disconnect', { h });
  const report = await request(test, 'test.end');
  if (report.openClients + report.openPipes + report.timers + report.pendingInvocations + report.subscriptions !== 0) {
    fail('leaks: ' + JSON.stringify(report));
  }
  step('test scope clean');
} else {
  step('no --address: skipped connect, invoke and pipe echo (start an interop server and pass its endpoint)');
}

await request(null, 'host.shutdown');
await new Promise((resolve) => setTimeout(resolve, 500));
socket.destroy();
server.close();
step('passed');
process.exit(0);
