import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { ListReplica } from '../../src/collections/ListReplica.js';
import { listMessageFormatter } from '../../src/collections/messages.js';
import { collectionTestCodec, fromHex, int32ArrayFormatter, intFormatter, loadVectors } from './support/codec.js';

/** @type {import('../../src/invocation/codec.js').InvocationCodec} */
let previousCodec;
beforeEach(() => {
  previousCodec = setInvocationCodec(collectionTestCodec);
});
afterEach(() => {
  setInvocationCodec(previousCodec);
});

const streams = /** @type {import('./support/codec.js').StreamVector[]} */ (loadVectors().filter((v) => v.kind === 'stream'));
const formatter = listMessageFormatter(intFormatter, int32ArrayFormatter);

const flush = async () => {
  for (let i = 0; i < 3; i++) await Promise.resolve();
};

// Each stream was fed to the C# client state machine (NexusListClient.OnProcess) item by item, with a request
// pending before every item. The JS replica must do exactly the same after each item.
describe('C# collection streams (fixtures/collections.jsonl)', () => {
  it('cover resets, every change, acks, noops, stale changes, protocol errors and closes', () => {
    const names = streams.map((s) => s.name.split('/')[0]);
    expect(new Set(names)).toEqual(new Set(['reset', 'ready', 'error', 'close']));
  });

  for (const stream of streams) {
    it(stream.name, async () => {
      /** @type {string[]} */
      let events = [];
      let synchronizedCount = 0;
      /** @type {ListReplica<number>} */
      const replica = new ListReplica({
        emit: (change) => events.push(change.action),
        synchronized: () => synchronizedCount++,
      });
      /** @type {boolean | null} */
      let answer = null;

      for (const [i, step] of stream.steps.entries()) {
        const where = `${stream.name} step ${i}`;
        events = [];
        if (!replica.hasPending) {
          answer = null;
          void replica.beginRequest().then((applied) => {
            answer = applied;
          });
        }
        /** @type {string | null} */
        let error = null;
        if (step.close) {
          replica.close();
          // NexusList raises reset when the pipe closes, as C# Disconnected does.
          events.push('reset');
        } else {
          const reader = collectionTestCodec.createReader(fromHex(/** @type {string} */ (step.hex)));
          error = replica.process(formatter.read(reader));
        }
        await flush();
        expect(error !== null, `${where}: protocol error ${error}`).toBe(step.disconnect ?? false);
        expect([...replica.items], where).toEqual(step.list);
        expect(replica.version, where).toBe(step.version);
        expect(events, where).toEqual(step.events);
        expect(answer, where).toBe(step.answer);
      }
      expect(synchronizedCount).toBeLessThanOrEqual(1);
    });
  }
});
