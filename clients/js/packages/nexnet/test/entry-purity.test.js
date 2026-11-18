import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { isNodeBuiltin, stripComments, walkImports } from './support/import-graph.js';

const src = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src');

describe('browser entry purity', () => {
  for (const entry of ['browser.js', 'index.js']) {
    it(`${entry} reaches no node: or Node built-in import`, () => {
      const graph = walkImports(path.join(src, entry));
      expect(graph.files.length).toBeGreaterThan(1);
      const offending = graph.external.filter((e) => isNodeBuiltin(e.specifier));
      expect(offending).toEqual([]);
    });

    it(`${entry} has no runtime dependencies`, () => {
      expect(walkImports(path.join(src, entry)).external).toEqual([]);
    });
  }

  it('node.js reaches the Node-only module', () => {
    const graph = walkImports(path.join(src, 'node.js'));
    expect(graph.files).toContain(path.join(src, 'contract', 'node.js'));
  });
});

describe('import-graph checker', () => {
  const root = path.resolve('/virtual');
  /** @param {Record<string, string>} files */
  const reader = (files) => (/** @type {string} */ file) => {
    const text = files[path.relative(root, file).replaceAll('\\', '/')];
    if (text === undefined) throw new Error(`missing ${file}`);
    return text;
  };

  it('finds a transitive node: import', () => {
    const graph = walkImports(path.join(root, 'entry.js'), reader({
      'entry.js': "export * from './a.js';",
      'a.js': "import { b } from './sub/b.js'; export const a = b;",
      'sub/b.js': "import net from 'node:net';\nexport const b = net;",
    }));
    expect(graph.files).toHaveLength(3);
    expect(graph.external.map((e) => e.specifier)).toEqual(['node:net']);
  });

  it('finds bare built-ins, side-effect, re-export and dynamic imports', () => {
    const graph = walkImports(path.join(root, 'entry.js'), reader({
      'entry.js': [
        "import 'fs';",
        "export { x } from 'tls';",
        "const m = await import('node:dns');",
        "import * as p from 'path/posix';",
      ].join('\n'),
    }));
    const specs = graph.external.map((e) => e.specifier);
    expect(specs.sort()).toEqual(['fs', 'node:dns', 'path/posix', 'tls']);
    expect(specs.every(isNodeBuiltin)).toBe(true);
  });

  it('ignores comments but keeps strings', () => {
    const code = "/** @type {import('node:net').Socket} */\n// import 'node:fs';\nconst u = 'ws://x'; import './a.js';";
    const stripped = stripComments(code);
    expect(stripped).not.toContain('node:');
    expect(stripped).toContain("'ws://x'");
    const graph = walkImports(path.join(root, 'entry.js'), reader({ 'entry.js': code, 'a.js': '' }));
    expect(graph.external).toEqual([]);
    expect(graph.files).toHaveLength(2);
  });

  it('does not flag packages that are not built-ins', () => {
    expect(isNodeBuiltin('vitest')).toBe(false);
    expect(isNodeBuiltin('@dtronix/nexnet')).toBe(false);
  });
});
