#!/usr/bin/env node
// Builds the per-PR change log of the v0.21 merge train (docs/articles/releases/v0.21.0-changes.md).
//
// Usage (from the repository root; needs the GitHub CLI `gh`, logged in, and git):
//   node tools/release/train-changelog.mjs [--out <file>] [--repo <owner/name>] [--base <branch>]
//                                          [--git-ref <ref>] [--master-since <ISO date>] [--version <x.y.z>]
//   node tools/release/train-changelog.mjs --stdout      # print instead of writing
//
// Sources:
//   - every PR merged into --base (default train/multilang);
//   - every PR merged into master after --master-since (default: the v0.20.0 release commit), i.e. the
//     master-side units (F02-F06, F14-F16) and hot-fixes (S28, S31, S35);
//   - the "Train: merge master" merge commits on --git-ref (default origin/train/multilang): the master -> train syncs.
// PRs are grouped by the unit prefix of their title ("[S28] ..." -> S) and sorted by unit ID, one line per PR.
// The output has no timestamps, so running it again only changes the file when PRs were merged. Re-run it after
// `git fetch origin` just before the release.

import { execFileSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';

const args = process.argv.slice(2);
function option(name, fallback) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
}

const repo = option('--repo', 'Dtronix/NexNet');
const base = option('--base', 'train/multilang');
const gitRef = option('--git-ref', 'origin/train/multilang');
const masterSince = option('--master-since', '2026-11-08T23:15:59Z'); // ec02d2b, the v0.20.0 release commit
const version = option('--version', '0.21.0');
const out = option('--out', `docs/articles/releases/v${version}-changes.md`);
const toStdout = args.includes('--stdout');

const groups = [
  ['F', 'Foundation, fixes and documentation', 'Toolchain, repository hygiene, protocol specification, docs catch-ups, and fixes that are not tied to one area.'],
  ['S', 'Server and C# runtime', 'Multi-endpoint server, lifecycle, transports, sessions, pipes and collections in the .NET library.'],
  ['G', 'Generator', 'Target-language IR, opt-in surface, diagnostics, bundle unpacking and the emitted client runtime.'],
  ['J', 'JavaScript client', 'The `@dtronix/nexnet` runtime, its emitters and the Node and browser hosts.'],
  ['K', 'Java client', 'The `com.dtronix.nexnet` runtime, its emitters, optional modules and the JVM host.'],
  ['H', 'Cross-language test harness', 'Test nexuses, golden vectors, the control protocol, drivers, host pool and CI workflows.'],
  ['T', 'Test framework migration', 'Microsoft.Testing.Platform, NUnit to TUnit, parallel-safe integration tests.'],
  ['P', 'Test porting, parity, docs and release', 'C# client tests ported to every language, public docs and the release.'],
  ['C', 'Contract amendments', 'Changes to frozen contracts (IR, runtime APIs, golden format, harness and control protocol).'],
];

function run(cmd, cmdArgs) {
  return execFileSync(cmd, cmdArgs, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}

function prs(extra) {
  const json = run('gh', ['pr', 'list', '--repo', repo, '--state', 'merged', '--limit', '1000',
    '--json', 'number,title,url,mergedAt', ...extra]);
  return JSON.parse(json);
}

const trainPrs = prs(['--base', base]).map(p => ({ ...p, side: 'train' }));
const masterPrs = prs(['--base', 'master', '--search', `merged:>${masterSince}`])
  .filter(p => new Date(p.mergedAt) > new Date(masterSince))
  .map(p => ({ ...p, side: 'master' }));

let syncs = [];
try {
  syncs = run('git', ['log', '--first-parent', '--merges', '--format=%H%x09%s', gitRef])
    .split('\n').filter(Boolean)
    .map(line => { const [sha, subject] = line.split('\t'); return { sha, subject }; })
    .filter(c => /^Train: merge master\b/.test(c.subject))
    .reverse();
} catch (e) {
  process.stderr.write(`warning: cannot read ${gitRef} (${e.message.split('\n')[0]}); the master syncs are left out\n`);
}

const ID = /^\[([A-Z]+-?\d+[a-z]?)\]\s*/;

function sortKey(id) {
  const m = /^([A-Z]+)-?(\d+)([a-z]?)$/.exec(id);
  return m ? [m[1], Number(m[2]), m[3]] : [id, 0, ''];
}

function compare(a, b) {
  const ka = sortKey(a.id), kb = sortKey(b.id);
  for (let i = 0; i < 3; i++) {
    if (ka[i] < kb[i]) return -1;
    if (ka[i] > kb[i]) return 1;
  }
  return a.number - b.number;
}

// Markdown-escape a PR title; backticks are kept so code spans render.
function escape(text) {
  return text.replace(/[\\*_<>[\]|]/g, c => '\\' + c);
}

const entries = [...trainPrs, ...masterPrs].map(p => {
  const m = ID.exec(p.title);
  return { ...p, id: m ? m[1] : '', text: m ? p.title.slice(m[0].length) : p.title };
});

const lines = [];
lines.push(`# NexNet v${version}: full change log`, '');
lines.push(`Every pull request merged for v${version}, grouped by work unit. Most were merged into the \`${base}\` branch; `
  + 'the ones marked *(master)* were merged into `master` first and then synced into the train. '
  + `The [release notes](release-notes-v${version}.md) explain what changed and whether you need to act.`);
lines.push('');
lines.push(`${entries.length} pull requests: ${trainPrs.length} into \`${base}\`, ${masterPrs.length} into \`master\`.`);
lines.push('', '<!-- Generated by tools/release/train-changelog.mjs. Do not edit by hand; run the script again. -->', '');

const used = new Set();
for (const [letter, title, about] of groups) {
  const items = entries.filter(e => e.id && sortKey(e.id)[0] === letter).sort(compare);
  if (items.length === 0) continue;
  lines.push(`## ${title} (${letter})`, '', about, '');
  for (const e of items) {
    used.add(e.number);
    const where = e.side === 'master' ? ' *(master)*' : '';
    lines.push(`- \\[${e.id}\\] ${escape(e.text)} ([#${e.number}](${e.url}))${where}`);
  }
  lines.push('');
}

const other = entries.filter(e => !used.has(e.number)).sort((a, b) => a.number - b.number);
if (other.length > 0) {
  lines.push('## Other', '');
  for (const e of other) {
    const id = e.id ? `\\[${e.id}\\] ` : '';
    const where = e.side === 'master' ? ' *(master)*' : '';
    lines.push(`- ${id}${escape(e.text)} ([#${e.number}](${e.url}))${where}`);
  }
  lines.push('');
}

if (syncs.length > 0) {
  lines.push('## Master to train syncs', '',
    'Merge commits that brought the master-side PRs above into the train.', '');
  for (const c of syncs) {
    lines.push(`- ${escape(c.subject)} ([${c.sha.slice(0, 8)}](https://github.com/${repo}/commit/${c.sha}))`);
  }
  lines.push('');
}

const text = lines.join('\n').replace(/\n+$/, '\n');
if (toStdout) {
  process.stdout.write(text);
} else {
  writeFileSync(out, text);
  process.stderr.write(`wrote ${out}: ${entries.length} PRs, ${syncs.length} syncs\n`);
}
