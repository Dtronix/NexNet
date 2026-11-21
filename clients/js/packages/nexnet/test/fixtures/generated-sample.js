/**
 * Hand-written sample of the code shape J04 generates for one export, used to check that the contract is usable
 * from generated code (typechecked by tsconfig.test.json and exercised by generated-sample.test.js).
 * The real emitter output is the J04 snapshot; this file only pins the runtime API it relies on.
 */

import { ClientNexusBase, MsgPackWriter, NexusClient, ProxyBase } from '@dtronix/nexnet';

/** @type {import('@dtronix/nexnet').Formatter<string>} */
const stringFormatter = {
  write: (w, v) => w.writeString(v),
  read: (r) => /** @type {string} */ (r.readString()),
};

/** @type {import('@dtronix/nexnet').HandshakeDescriptor} */
export const SampleHandshake = Object.freeze({
  version: null,
  serverNexusHash: -1288190245,
  clientNexusHash: 401298355,
  acceptedIds: new Set([0, 1]),
});

export class SampleServerProxy extends ProxyBase {
  /**
   * C# `ValueTask<string> Echo(string value, CancellationToken ct)`.
   * @param {string} value
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<string>}
   */
  echo(value, options) {
    return this._invoke(0, (w) => {
      w.writeArrayHeader(1);
      stringFormatter.write(w, value);
    }, { signal: options?.signal, returns: stringFormatter });
  }

  /**
   * C# `void Notify()`.
   * @returns {void}
   */
  notify() {
    this._invokeVoid(1, null);
  }

  /**
   * C# `ValueTask Upload(INexusDuplexPipe pipe)`.
   * @param {import('@dtronix/nexnet').NexusDuplexPipe} pipe
   * @returns {Promise<void>}
   */
  upload(pipe) {
    return this._invokePipe(2, pipe, (w, pipeId) => {
      w.writeArrayHeader(1);
      w.writeInt(pipeId);
    });
  }

  /** @returns {import('@dtronix/nexnet').NexusList<string>} */
  get names() {
    return this._collection(3, stringFormatter);
  }
}

/**
 * @abstract
 * @extends {ClientNexusBase<SampleServerProxy>}
 */
export class SampleClientNexusBase extends ClientNexusBase {
  /**
   * @abstract
   * @param {string} text
   * @param {{ signal: AbortSignal }} options
   * @returns {Promise<string>}
   */
  shout(text, options) {
    throw new Error('abstract');
  }

  /**
   * @protected
   * @param {number} methodId
   * @param {import('@dtronix/nexnet').MsgPackReader} reader
   * @param {import('@dtronix/nexnet').DispatchContext} ctx
   * @returns {void | Promise<void>}
   */
  _dispatch(methodId, reader, ctx) {
    switch (methodId) {
      case 0:
        return (async () => {
          reader.readArrayHeader();
          const text = stringFormatter.read(reader);
          ctx.writeResult(stringFormatter, await this.shout(text, { signal: ctx.signal }));
        })();
      default:
        return super._dispatch(methodId, reader, ctx);
    }
  }
}

/**
 * The generated factory.
 * @template {SampleClientNexusBase} TNexus
 * @param {import('@dtronix/nexnet').ClientConfig} config
 * @param {TNexus} nexus
 * @returns {NexusClient<TNexus, SampleServerProxy>}
 */
export function createSampleClient(config, nexus) {
  return NexusClient.create(config, nexus, { handshake: SampleHandshake, proxy: SampleServerProxy });
}

export { MsgPackWriter };
