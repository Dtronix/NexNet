import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import * as viaPackage from '@dtronix/nexnet';
import * as viaNodeSubpath from '@dtronix/nexnet/node';
import * as nodeEntry from '../src/node.js';
import { getInvocationCounters } from '../src/invocation/diagnostics.js';
import { getSessionCounters } from '../src/session/diagnostics.js';

const pkgDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(pkgDir, '../../../..');
const pkg = JSON.parse(readFileSync(path.join(pkgDir, 'package.json'), 'utf8'));

describe('package.json', () => {
  it('is an ESM package without side effects or runtime dependencies', () => {
    expect(pkg.name).toBe('@dtronix/nexnet');
    expect(pkg.type).toBe('module');
    expect(pkg.sideEffects).toBe(false);
    expect(pkg.engines.node).toBe('>=22');
    expect(pkg.dependencies ?? {}).toEqual({});
    expect(pkg.peerDependencies ?? {}).toEqual({});
  });

  it('versions in lockstep with the NexNet NuGet package (D19)', () => {
    const csproj = readFileSync(path.join(repoRoot, 'src/NexNet/NexNet.csproj'), 'utf8');
    const version = /<Version>([^<]+)<\/Version>/.exec(csproj)?.[1];
    expect(version).toBeDefined();
    expect(pkg.version).toBe(version);
  });

  it('exports map has the contract shape and its source files exist', () => {
    expect(Object.keys(pkg.exports['.'])).toEqual(['types', 'browser', 'node', 'default']);
    expect(Object.keys(pkg.exports['./node'])).toEqual(['types', 'node']);
    expect(Object.keys(pkg.exports['./diagnostics'])).toEqual(['types', 'default']);
    for (const entry of [pkg.exports['.'], pkg.exports['./node'], pkg.exports['./diagnostics']]) {
      for (const [condition, target] of Object.entries(entry)) {
        if (condition === 'types') continue; // produced by npm run build:types
        expect(existsSync(path.join(pkgDir, /** @type {string} */ (target))), target).toBe(true);
      }
    }
  });
});

describe('package resolution in Node', () => {
  it("'@dtronix/nexnet' resolves to the Node entry", () => {
    expect(viaPackage.NexusClient).toBe(nodeEntry.NodeNexusClient);
  });

  it("'@dtronix/nexnet/node' resolves to the Node entry", () => {
    expect(viaNodeSubpath.NexusClient).toBe(nodeEntry.NodeNexusClient);
  });

  it("'@dtronix/nexnet/diagnostics' exports the leak-audit counters", async () => {
    const specifier = '@dtronix/nexnet/diagnostics'; // a variable: tsconfig.test.json maps only the main entries
    const diagnostics = await import(specifier);
    expect(Object.keys(diagnostics).sort()).toEqual(['getInvocationCounters', 'getSessionCounters']);
    expect(diagnostics.getSessionCounters).toBe(getSessionCounters);
    expect(diagnostics.getInvocationCounters).toBe(getInvocationCounters);
    expect(Object.keys(diagnostics.getSessionCounters()).sort()).toEqual(['openSessions', 'pendingWrites', 'timers']);
    expect(Object.keys(diagnostics.getInvocationCounters()).sort())
      .toEqual(['creditWaiters', 'inboundInvocations', 'largeMessages', 'pendingInvocations', 'reservedInvocationIds']);
  });
});
