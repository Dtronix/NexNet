/**
 * The launcher of the browser language host (control protocol §2, "Browser hosts"; `artifacts/interop/browser-<engine>/host.json`):
 *
 *   node launcher.js --control 127.0.0.1:<port> --token <token> --browser chromium|firefox|webkit
 *
 * 1. Bundles the page (`src/page.js`), the host core and the generated test scaffolding
 *    (`artifacts/interop/js/generated`) for the browser with Vite.
 * 2. Serves the page on a loopback HTTP server whose `/bridge` WebSocket relays bytes to and from the runner's control
 *    port unchanged.
 * 3. Opens the page in a Playwright browser (the context trusts the test server certificates). The page sends `hello`.
 *
 * It exits when the control connection or the bridge closes.
 * @module
 */

import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { acceptWebSocket } from './ws.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const packageDir = path.resolve(here, '..', '..');
const jsDir = path.resolve(packageDir, '..', '..');
const repoRoot = process.env.NEXNET_REPO_ROOT ? path.resolve(process.env.NEXNET_REPO_ROOT) : path.resolve(jsDir, '..', '..');
const generatedDir = process.env.NEXNET_INTEROP_JS_GENERATED
  ? path.resolve(process.env.NEXNET_INTEROP_JS_GENERATED)
  : path.join(repoRoot, 'artifacts', 'interop', 'js', 'generated');
const formattersDir = path.join(jsDir, 'packages', 'interop-host-node', 'src', 'formatters');
const runtimeDir = path.join(jsDir, 'packages', 'nexnet');

/** @param {string} p @returns {string} */
const slash = (p) => p.replace(/\\/g, '/');

/**
 * @param {readonly string[]} argv
 * @returns {Record<string, string>}
 */
function parseArgs(argv) {
  /** @type {Record<string, string>} */
  const args = {};
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].startsWith('--') && i + 1 < argv.length) args[argv[i].slice(2)] = argv[++i];
  }
  return args;
}

/** @param {string} message */
function log(message) {
  process.stderr.write('[browser host] ' + message + '\n');
}

/**
 * Bundles the page into `dir`; returns the bundle's path.
 * @param {string} dir
 * @returns {Promise<string>}
 */
async function bundle(dir) {
  const { build } = await import('vite');
  const scaffolding = path.join(generatedDir, 'scaffolding');
  const entry = path.join(dir, 'entry.js');
  fs.writeFileSync(entry, [
    "import * as scaffolding from '" + slash(path.join(scaffolding, 'index.js')) + "';",
    "import * as codecs from '" + slash(path.join(scaffolding, 'test-codecs.js')) + "';",
    "import { startPage } from '" + slash(path.join(packageDir, 'src', 'page.js')) + "';",
    'startPage({ schemaHash: scaffolding.SCHEMA_HASH, find: scaffolding.find, forType: codecs.forType });',
    '',
  ].join('\n'));
  const out = path.join(dir, 'dist');
  await build({
    configFile: false,
    root: dir,
    logLevel: 'warn',
    resolve: {
      alias: [
        { find: /^@dtronix\/nexnet$/, replacement: slash(path.join(runtimeDir, 'src', 'browser.js')) },
        { find: /^@dtronix\/nexnet\/diagnostics$/, replacement: slash(path.join(runtimeDir, 'src', 'diagnostics.js')) },
      ],
    },
    plugins: [
      {
        name: 'nexnet-generated-formatters',
        // User formatters (D12) the generated code imports from its own directory live in the Node host package.
        resolveId(source, importer) {
          if (!importer || !(source.startsWith('./') || source.startsWith('../'))) return null;
          const resolved = path.resolve(path.dirname(importer), source);
          if (!slash(resolved).startsWith(slash(generatedDir)) || fs.existsSync(resolved)) return null;
          const replacement = path.join(formattersDir, path.basename(resolved));
          return fs.existsSync(replacement) ? replacement : null;
        },
      },
    ],
    build: {
      outDir: out,
      emptyOutDir: true,
      minify: false,
      sourcemap: false,
      target: 'es2022',
      lib: { entry, formats: ['es'], fileName: () => 'bundle.js' },
    },
  });
  return path.join(out, 'bundle.js');
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const control = args.control;
  const token = args.token;
  const engineName = args.browser ?? 'chromium';
  if (!control || !token) {
    log('usage: node launcher.js --control 127.0.0.1:<port> --token <token> [--browser chromium|firefox|webkit]');
    process.exit(2);
  }
  if (!fs.existsSync(path.join(generatedDir, 'scaffolding', 'index.js'))) {
    log('The generated test scaffolding (' + generatedDir + ') is missing. Build it: dotnet build src/NexNet.TestNexus');
    process.exit(3);
  }

  const playwright = await import('playwright');
  /** @type {Record<string, import('playwright').BrowserType>} */
  const engines = { chromium: playwright.chromium, firefox: playwright.firefox, webkit: playwright.webkit };
  const engine = engines[engineName];
  if (!engine) {
    log('Unknown browser ' + engineName + ' (chromium, firefox, webkit).');
    process.exit(2);
  }

  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'nexnet-browser-host-'));
  /** @type {import('playwright').Browser | null} */
  let browser = null;
  let exiting = false;
  /** @param {number} code */
  const exit = async (code) => {
    if (exiting) return;
    exiting = true;
    try {
      await Promise.race([browser?.close(), new Promise((resolve) => setTimeout(resolve, 5000))]);
    } catch {
      // Exiting anyway.
    }
    try {
      fs.rmSync(work, { recursive: true, force: true });
    } catch {
      // A temp directory left behind.
    }
    process.exit(code);
  };
  process.on('SIGTERM', () => void exit(0));
  process.on('SIGINT', () => void exit(0));

  const bundlePath = await bundle(work);
  const runtimeVersion = JSON.parse(fs.readFileSync(path.join(runtimeDir, 'package.json'), 'utf8')).version;

  const server = http.createServer((request, response) => {
    const url = new URL(request.url ?? '/', 'http://localhost');
    if (url.pathname === '/bundle.js') {
      response.writeHead(200, { 'content-type': 'text/javascript; charset=utf-8', 'cache-control': 'no-store' });
      response.end(fs.readFileSync(bundlePath));
    } else if (url.pathname === '/') {
      response.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
      response.end('<!doctype html><html><head><meta charset="utf-8"><title>NexNet browser host</title></head>'
        + '<body><script type="module" src="/bundle.js"></script></body></html>');
    } else {
      response.writeHead(404);
      response.end();
    }
  });

  let bridged = false;
  server.on('upgrade', (request, socket, head) => {
    if (bridged || new URL(request.url ?? '/', 'http://localhost').pathname !== '/bridge') {
      socket.destroy();
      return;
    }
    bridged = true;
    const separator = control.lastIndexOf(':');
    const tcp = net.connect({ host: control.slice(0, separator), port: Number(control.slice(separator + 1)) });
    tcp.setNoDelay(true);
    const ws = acceptWebSocket(request, socket, head, (payload) => tcp.write(payload), () => {
      log('the page closed the bridge');
      tcp.end();
      void exit(0);
    });
    if (!ws) {
      tcp.destroy();
      void exit(1);
      return;
    }
    tcp.on('data', (chunk) => ws.send(chunk));
    tcp.on('close', () => {
      ws.close();
      void exit(0);
    });
    tcp.on('error', (error) => log('control connection error: ' + error.message));
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(undefined)));
  const port = /** @type {net.AddressInfo} */ (server.address()).port;

  browser = await engine.launch({
    headless: true,
    args: engineName === 'chromium' ? ['--ignore-certificate-errors'] : [],
  });
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  page.on('console', (message) => {
    if (message.type() === 'error' || message.type() === 'warning') log('console.' + message.type() + ': ' + message.text());
  });
  page.on('pageerror', (error) => log('page error: ' + (error.stack ?? error.message)));
  page.on('crash', () => {
    log('the page crashed');
    void exit(1);
  });
  browser.on('disconnected', () => void exit(exiting ? 0 : 1));

  const query = new URLSearchParams({
    bridge: 'ws://127.0.0.1:' + port + '/bridge',
    token,
    lang: 'browser-' + engineName,
    runtime: engineName + ' ' + browser.version(),
    lib: '@dtronix/nexnet ' + runtimeVersion,
  });
  await page.goto('http://127.0.0.1:' + port + '/?' + query.toString());
}

main().catch((error) => {
  log('failed to start: ' + (error?.stack ?? String(error)));
  process.exit(1);
});
