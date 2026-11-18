/**
 * Static import-graph walker for the entry-purity tests. Follows relative static imports, re-exports and literal
 * dynamic imports from an entry file, and reports every non-relative specifier it reaches.
 * Comments are stripped first, so JSDoc `import('node:net')` type references are ignored.
 */

import { readFileSync } from 'node:fs';
import { builtinModules } from 'node:module';
import path from 'node:path';

const builtins = new Set(builtinModules);

/**
 * True for `node:*` and bare Node built-in names (`fs`, `net/promises`, ...).
 * @param {string} specifier
 * @returns {boolean}
 */
export function isNodeBuiltin(specifier) {
  if (specifier.startsWith('node:')) return true;
  return builtins.has(specifier) || builtins.has(specifier.split('/')[0]);
}

/**
 * Removes // and block comments, leaving string and template literals intact.
 * @param {string} source
 * @returns {string}
 */
export function stripComments(source) {
  let out = '';
  let i = 0;
  while (i < source.length) {
    const c = source[i];
    const next = source[i + 1];
    if (c === '/' && next === '/') {
      while (i < source.length && source[i] !== '\n') i++;
    } else if (c === '/' && next === '*') {
      const end = source.indexOf('*/', i + 2);
      i = end < 0 ? source.length : end + 2;
      out += ' ';
    } else if (c === '"' || c === "'" || c === '`') {
      const start = i++;
      while (i < source.length && source[i] !== c) {
        if (source[i] === '\\') i++;
        i++;
      }
      i++;
      out += source.slice(start, i);
    } else {
      out += c;
      i++;
    }
  }
  return out;
}

const patterns = [
  /\bimport\s+(?:[\w*{}\s,$]+?\s+from\s+)?(['"])([^'"]+)\1/g,    // import x from 'a'; import 'a'
  /\bexport\s+(?:\*(?:\s+as\s+\w+)?|\{[^}]*\})\s+from\s+(['"])([^'"]+)\1/g, // export * / { } from 'a'
  /\bimport\s*\(\s*(['"])([^'"]+)\1\s*\)/g,                       // import('a')
];

/**
 * @param {string} source Comment-free source.
 * @returns {string[]} Module specifiers in source order.
 */
export function findSpecifiers(source) {
  /** @type {string[]} */
  const found = [];
  for (const re of patterns) {
    for (const m of source.matchAll(re)) found.push(m[2]);
  }
  return found;
}

/**
 * Walks the graph from `entry`.
 * @param {string} entry Absolute file path.
 * @param {(file: string) => string} [read] File reader (tests pass a virtual one).
 * @returns {{ files: string[], external: { specifier: string, from: string }[] }}
 */
export function walkImports(entry, read = (f) => readFileSync(f, 'utf8')) {
  /** @type {Set<string>} */
  const seen = new Set();
  /** @type {{ specifier: string, from: string }[]} */
  const external = [];
  const queue = [path.resolve(entry)];
  while (queue.length > 0) {
    const file = /** @type {string} */ (queue.shift());
    if (seen.has(file)) continue;
    seen.add(file);
    for (const specifier of findSpecifiers(stripComments(read(file)))) {
      if (specifier.startsWith('./') || specifier.startsWith('../')) {
        queue.push(path.resolve(path.dirname(file), specifier));
      } else {
        external.push({ specifier, from: file });
      }
    }
  }
  return { files: [...seen], external };
}
