/**
 * `npm run build:interop-host` (alias `build:host`): writes the launch configurations of the JavaScript language hosts
 * (control protocol §10): `artifacts/interop/node/host.json` and `artifacts/interop/browser-<engine>/host.json` (plus
 * the `browser` fallback). The hosts load the generated code from `artifacts/interop/js/generated` when they start,
 * so a rebuild of `src/NexNet.TestNexus` needs no host rebuild.
 * @module
 */

import fs from 'node:fs';
import path from 'node:path';
import { repoRoot, generatedDir, GENERATE_COMMAND } from './paths.js';

/**
 * @param {string} language
 * @param {Record<string, unknown>} config
 */
function writeHostJson(language, config) {
  const dir = path.join(repoRoot, 'artifacts', 'interop', language);
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, 'host.json');
  fs.writeFileSync(file, JSON.stringify(config, null, 2) + '\n');
  process.stdout.write('wrote ' + path.relative(repoRoot, file) + '\n');
}

writeHostJson('node', {
  command: 'node',
  args: ['clients/js/packages/interop-host-node/src/main.js'],
  workingDir: '.',
  env: { NODE_OPTIONS: '--enable-source-maps' },
  maxConcurrency: 4,
  startupTimeoutMs: 60000,
});

const browser = {
  command: 'node',
  args: ['clients/js/packages/interop-host-browser/src/node/launcher.js'],
  workingDir: '.',
  maxConcurrency: 4,
  startupTimeoutMs: 120000,
};
for (const language of ['browser', 'browser-chromium', 'browser-firefox', 'browser-webkit']) writeHostJson(language, browser);

if (!fs.existsSync(path.join(generatedDir, 'scaffolding', 'index.js'))) {
  process.stdout.write('note: the generated test scaffolding is not built yet (' + path.relative(repoRoot, generatedDir)
    + '); building the tests builds it, or run: ' + GENERATE_COMMAND + '\n');
}
