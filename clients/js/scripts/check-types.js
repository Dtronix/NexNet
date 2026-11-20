// Checks that the committed declarations of @dtronix/nexnet (packages/nexnet/types, J27) are current: rebuilds them
// with tsc into a temporary folder and compares file by file (line endings ignored). Run by `npm run typecheck`;
// `npm run build:types` rewrites them.
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const committed = path.join(workspace, 'packages', 'nexnet', 'types');

/** @param {string} root @param {string} [dir] @returns {string[]} '/' separated paths of every file under root. */
function list(root, dir = '') {
  /** @type {string[]} */
  const files = [];
  let entries;
  try {
    entries = readdirSync(path.join(root, dir), { withFileTypes: true });
  } catch {
    return files;
  }
  for (const entry of entries) {
    const rel = dir ? dir + '/' + entry.name : entry.name;
    if (entry.isDirectory()) files.push(...list(root, rel));
    else files.push(rel);
  }
  return files.sort();
}

/** @param {string} file */
const read = (file) => readFileSync(file, 'utf8').replace(/\r\n/g, '\n');

const fresh = mkdtempSync(path.join(tmpdir(), 'nexnet-types-'));
try {
  const require = createRequire(path.join(workspace, 'package.json'));
  const typescript = path.dirname(require.resolve('typescript/package.json'));
  const bin = JSON.parse(readFileSync(path.join(typescript, 'package.json'), 'utf8')).bin.tsc;
  const tsc = spawnSync(process.execPath, [path.join(typescript, bin), '-p', 'tsconfig.json', '--outDir', fresh], { cwd: workspace, stdio: 'inherit' });
  if (tsc.status !== 0) {
    console.error('check-types: tsc failed');
    process.exit(1);
  }

  const expected = list(fresh);
  const actual = list(committed);
  const problems = [];
  for (const file of expected) {
    if (!actual.includes(file)) problems.push('missing: ' + file);
    else if (read(path.join(fresh, file)) !== read(path.join(committed, file))) problems.push('stale: ' + file);
  }
  for (const file of actual) {
    if (!expected.includes(file)) problems.push('extra: ' + file);
  }

  if (problems.length > 0) {
    console.error('check-types: packages/nexnet/types is not current. Run \'npm run build:types --prefix clients/js\' and commit.');
    for (const problem of problems) console.error('  ' + problem);
    process.exit(1);
  }
  console.log('check-types: ' + expected.length + ' declaration files are current');
} finally {
  rmSync(fresh, { recursive: true, force: true });
}
