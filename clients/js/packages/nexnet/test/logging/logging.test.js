import { describe, expect, it } from 'vitest';
import { ConsoleLogger, NexusLogBehaviors, RollingLogger, describeError } from '../../src/logging/index.js';

/** A clock that returns the values it is given. */
function steppingClock(/** @type {number[]} */ ...values) {
  let i = 0;
  return () => values[Math.min(i++, values.length - 1)];
}

describe('RollingLogger', () => {
  it('formats lines like C# CoreLogger: [seconds][LVL] [path(category)] message', () => {
    const root = new RollingLogger({ now: steppingClock(1000, 2500), pathSegment: 'CL1' });
    root.child('Session 3').log('information', 'connected');
    expect(root.lines()).toEqual(['[1.500000][INF] [CL1|Session 3] connected']);
    expect(root.format('warning', 'w', null, 'Cat')).toMatch(/^\[\d+\.\d{6}\]\[WAR\] \[CL1\(Cat\)\] w$/);
  });

  it('shares the minimum level, behaviors and buffer across the tree; renaming a segment renames the descendants', () => {
    const root = new RollingLogger({ maxLines: 10, minLevel: 'information' });
    const child = root.child('A');
    const grandchild = child.child('B');
    grandchild.log('debug', 'hidden');
    grandchild.log('error', 'shown', new Error('boom'));
    expect(root.totalLinesWritten).toBe(1);
    expect(root.lines()[0]).toMatch(/\[ERR\] \[A\|B\] shown Error: boom/);
    child.minLevel = 'trace';
    expect(root.minLevel).toBe('trace');
    child.behaviors = NexusLogBehaviors.LogTransportData;
    expect(root.behaviors).toBe(NexusLogBehaviors.LogTransportData);
    child.pathSegment = 'A2';
    expect(grandchild.formattedPath).toBe('A2|B');
    child.pathSegment = '  ';
    expect(grandchild.formattedPath).toBe('B');
    root.minLevel = 'none';
    grandchild.log('critical', 'off');
    expect(root.totalLinesWritten).toBe(1);
    root.minLevel = 'trace';
    root.enabled = false;
    grandchild.log('critical', 'off');
    expect(root.totalLinesWritten).toBe(1);
    expect(() => { root.minLevel = /** @type {any} */ ('loud'); }).toThrow(TypeError);
  });

  it('rolls over and flushes oldest first with a truncation notice, then clears', () => {
    const logger = new RollingLogger({ maxLines: 3 });
    for (let i = 1; i <= 5; i++) logger.log('information', `m${i}`);
    /** @type {string[]} */
    const out = [];
    logger.flush((line) => out.push(line));
    expect(out[0]).toBe('Truncating Log. Showing only last 3 out of 5 total lines written.');
    expect(out.slice(1).map((l) => l.split('] ').pop())).toEqual(['m3', 'm4', 'm5']);
    expect(logger.totalLinesWritten).toBe(0);
    logger.flush((line) => out.push(line));
    expect(out.length).toBe(4);
    expect(() => new RollingLogger({ maxLines: 0 })).toThrow(RangeError);
  });

  it('never writes the bytes of a byte array (tokens)', () => {
    const logger = new RollingLogger();
    const token = new TextEncoder().encode('secret-token');
    logger.log('error', 'auth failed', token);
    expect(logger.lines()[0]).toContain('<Uint8Array 12 bytes>');
    expect(logger.lines()[0]).not.toContain('115');
    expect(describeError(new ArrayBuffer(4))).toBe('<ArrayBuffer 4 bytes>');
    expect(describeError('plain')).toBe('plain');
  });
});

describe('ConsoleLogger', () => {
  it('routes each level to the matching console method', () => {
    /** @type {Array<[string, string]>} */
    const calls = [];
    const fake = {
      debug: (/** @type {string} */ l) => calls.push(['debug', l]),
      info: (/** @type {string} */ l) => calls.push(['info', l]),
      warn: (/** @type {string} */ l) => calls.push(['warn', l]),
      error: (/** @type {string} */ l) => calls.push(['error', l]),
    };
    const logger = new ConsoleLogger({ console: fake }).child('Client');
    for (const level of /** @type {const} */ (['trace', 'debug', 'information', 'warning', 'error', 'critical'])) logger.log(level, level);
    expect(calls.map(([m]) => m)).toEqual(['debug', 'debug', 'info', 'warn', 'error', 'error']);
    expect(calls[2][1]).toMatch(/\[INF\] \[Client\] information$/);
    expect(logger.isEnabled('trace')).toBe(true);
  });
});
