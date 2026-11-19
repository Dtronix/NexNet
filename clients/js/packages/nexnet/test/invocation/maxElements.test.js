import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { MsgPackWriter } from '../../src/contract/codec.js';
import { RemoteExceptionDetail } from '../../src/contract/enums.js';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { getInvocationCounters } from '../../src/invocation/diagnostics.js';
import { getInvocationManager } from '../../src/invocation/InvocationManager.js';
import {
  LargeMessageKind,
  ResultState,
  decodeInvocationResult,
  encodeInvocation,
  encodeInvocationResult,
  tryReadRemoteErrorDetail,
} from '../../src/invocation/messages.js';
import { NexusSerializationError } from '../../src/serialization/errors.js';
import { NexusSerializerOptions } from '../../src/serialization/MsgPackReader.js';
import { MIN_DEFAULT_MAX_ELEMENTS, defaultMaxElements, resolveClientOptions } from '../../src/session/options.js';
import { MessageType } from '../../src/session/protocol.js';
import { connectedHarness, tick, until } from './support/harness.js';

// The session supplies the reader's maxElements: max(65536, maxMessageSize / 16) unless configured (spec §11, J21).

/** @type {import('../../src/invocation/codec.js').InvocationCodec} */
let previousCodec;
beforeEach(() => {
  // The real MsgPackReader enforces maxElements; the test codec does not.
  previousCodec = setInvocationCodec(null);
});
afterEach(() => {
  setInvocationCodec(previousCodec);
});

const MiB = 1024 * 1024;
const zeroCounters = () => ({ pendingInvocations: 0, reservedInvocationIds: 0, creditWaiters: 0, inboundInvocations: 0, largeMessages: 0 });

/** @param {Awaited<ReturnType<typeof connectedHarness>>} h */
async function finish(h) {
  await h.client.disconnect();
  await tick();
  expect(getInvocationCounters()).toEqual(zeroCounters());
}

/**
 * An array of `count` nils: one element per byte, the densest payload.
 * @param {number} count
 * @returns {Uint8Array}
 */
function nils(count) {
  const w = new MsgPackWriter();
  w.writeArrayHeader(count);
  for (let i = 0; i < count; i++) w.writeNil();
  return w.toUint8Array().slice();
}

/** @type {import('../../src/contract/codec.js').Formatter<number>} Reads an array of nils, returns its length. */
const nilsFormatter = {
  write: () => { throw new Error('not used'); },
  read: (r) => {
    const n = r.readArrayHeader();
    for (let i = 0; i < n; i++) r.readNil();
    return n;
  },
};

/**
 * Streams `body` as a large message in chunks of one frame or less.
 * @param {Awaited<ReturnType<typeof connectedHarness>>} h
 * @param {number} invocationId
 * @param {number} kind
 * @param {number} innerType
 * @param {Uint8Array} body
 */
function stream(h, invocationId, kind, innerType, body) {
  h.sendStart({ invocationId, kind, innerType, totalLength: body.length });
  for (let offset = 0; offset < body.length; offset += 60_000) h.sendChunk(invocationId, kind, body.subarray(offset, offset + 60_000));
}

/** @param {Awaited<ReturnType<typeof connectedHarness>>} h */
const results = (h) => h.framesOf(MessageType.InvocationResult).map((f) => decodeInvocationResult(f.body));

describe('default maxElements (spec §11)', () => {
  const tcp = { tcp: { host: 'localhost', port: 1 } };
  const supported = { tcp: () => null };
  /** @param {Record<string, unknown>} config */
  const resolve = (config) => resolveClientOptions(/** @type {any} */ ({ ...tcp, ...config }), supported).serializerOptions;

  it('is max(65536, maxMessageSize / 16) when unset, as C# ConfigBase.SerializerOptions', () => {
    expect(defaultMaxElements(64 * 1024)).toBe(MIN_DEFAULT_MAX_ELEMENTS);
    expect(defaultMaxElements(MiB)).toBe(65_536);
    expect(defaultMaxElements(MiB + 16 * 7)).toBe(65_543);
    expect(defaultMaxElements(MiB + 15)).toBe(65_536);
    expect(resolve({}).maxElements).toBe(262_144); // 4 MiB untrusted default
    expect(resolve({ serializerOptions: { preset: 'trusted' }, sessionBudget: 64 * MiB }).maxElements).toBe(MiB); // 16 MiB trusted default
    expect(resolve({ maxMessageSize: 64 * 1024 }).maxElements).toBe(65_536);
    expect(resolve({ maxMessageSize: 8 * MiB, sessionBudget: 32 * MiB }).maxElements).toBe(524_288);
  });

  it('keeps the rest of the configured options; 0 means the default, as in C#; an explicit limit wins', () => {
    expect(resolve({ serializerOptions: { maxDepth: 5, strictUtf8: false } })).toEqual({ maxDepth: 5, strictUtf8: false, maxElements: 262_144 });
    expect(resolve({ serializerOptions: { maxElements: 0 } }).maxElements).toBe(262_144);
    expect(resolve({ serializerOptions: { maxElements: null } }).maxElements).toBe(262_144);
    const configured = { maxElements: 10 };
    expect(resolve({ serializerOptions: configured })).toBe(configured);
    expect(resolve({ serializerOptions: NexusSerializerOptions.Trusted, sessionBudget: 64 * MiB }).maxElements).toBe(MiB);
    // A malformed value is left for the reader to reject.
    expect(resolve({ serializerOptions: { maxElements: -1 } }).maxElements).toBe(-1);
  });
});

describe('readers created by the session enforce the default maxElements', () => {
  // maxMessageSize 1 MiB: the limit is 65,536, so a value over it needs more than one frame.
  const config = { maxMessageSize: MiB };

  it('the invocation manager carries the resolved options', async () => {
    const h = await connectedHarness({ config });
    expect(getInvocationManager(h.session)?.serializerOptions.maxElements).toBe(65_536);
    await finish(h);
  });

  it('a result at the limit decodes; one element over rejects the call with NexusSerializationError (elements)', async () => {
    const h = await connectedHarness({ config });
    const ok = h.proxy.invoke(1, null, { returns: nilsFormatter });
    const over = h.proxy.invoke(2, null, { returns: nilsFormatter });
    await tick();
    // The array header counts its own elements: 65,536 in total is the most allowed.
    stream(h, 1, LargeMessageKind.Result, MessageType.InvocationResult, encodeInvocationResult(1, ResultState.CompletedResult, nils(65_536)));
    stream(h, 2, LargeMessageKind.Result, MessageType.InvocationResult, encodeInvocationResult(2, ResultState.CompletedResult, nils(65_537)));
    await expect(ok).resolves.toBe(65_536);
    const error = await over.then(() => null, (e) => e);
    expect(error).toBeInstanceOf(NexusSerializationError);
    expect(error.kind).toBe('elements');
    expect(h.session.isDisconnecting).toBe(false);
    await finish(h);
  });

  it('arguments over the limit fail the dispatched method; the client answers with an Exception result', async () => {
    const h = await connectedHarness({ config: { ...config, remoteExceptionDetail: RemoteExceptionDetail.TypeAndMessage } });
    /** @type {unknown[]} */
    const errors = [];
    /** @type {number[]} */
    const lengths = [];
    h.nexus.methods.set(0, (reader) => {
      try {
        lengths.push(nilsFormatter.read(reader));
      } catch (e) {
        errors.push(e);
        throw e;
      }
    });
    stream(h, 1, LargeMessageKind.Argument, MessageType.Invocation, encodeInvocation(1, 0, 0, nils(65_536)));
    await until(() => results(h).length === 1, 200);
    stream(h, 2, LargeMessageKind.Argument, MessageType.Invocation, encodeInvocation(2, 0, 0, nils(65_537)));
    await until(() => results(h).length === 2, 200);
    expect(lengths).toEqual([65_536]);
    expect(errors.length).toBe(1);
    expect(errors[0]).toBeInstanceOf(NexusSerializationError);
    expect(/** @type {NexusSerializationError} */ (errors[0]).kind).toBe('elements');
    const [first, second] = results(h);
    expect(first.state).toBe(ResultState.CompletedResult);
    expect(second.state).toBe(ResultState.Exception);
    expect(tryReadRemoteErrorDetail(second.value)?.type).toBe('NexusSerializationError');
    expect(h.session.isDisconnecting).toBe(false);
    await finish(h);
  });
});
