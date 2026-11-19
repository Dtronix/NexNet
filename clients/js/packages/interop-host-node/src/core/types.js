/**
 * Shapes of the generated test scaffolding (J11) that the host uses, so the core type-checks without the generated
 * code (`artifacts/interop/js/generated/scaffolding`).
 * @module
 */

/**
 * A typed-JSON codec of the scaffolding (`TypedCodec`).
 * @typedef {object} TypedCodec
 * @property {string} expression The golden type expression.
 * @property {import('@dtronix/nexnet').Formatter<any>} formatter
 * @property {(value: any) => unknown} toJson
 * @property {(json: unknown) => any} fromJson
 */

/**
 * One call of a client nexus slot (`ScriptCall`).
 * @typedef {object} ScriptCall
 * @property {string} slot
 * @property {readonly unknown[]} args
 * @property {AbortSignal | null} signal
 * @property {import('@dtronix/nexnet').NexusDuplexPipe | import('@dtronix/nexnet').NexusDuplexChannel<any> | null} pipe
 * @property {TypedCodec | null} channelItem
 */

/**
 * @typedef {object} ScriptHost
 * @property {(call: ScriptCall) => unknown} call
 */

/**
 * A server method (`Method`).
 * @typedef {object} Method
 * @property {string} name
 * @property {number} id
 * @property {'Void' | 'Task' | 'TaskOf'} returnKind
 * @property {'None' | 'Pipe' | 'Channel'} pipeKind
 * @property {boolean} cancellable
 * @property {TypedCodec | null} result
 * @property {TypedCodec | null} channelItem
 */

/**
 * A synchronized collection (`Collection`).
 * @typedef {object} Collection
 * @property {string} name
 * @property {number} id
 * @property {number} mode
 * @property {TypedCodec} item
 */

/**
 * The scaffolding of one exported nexus pair (`ScaffoldingExport`).
 * @typedef {object} ScaffoldingExport
 * @property {string} clientNexus
 * @property {string} serverNexus
 * @property {any} binding
 * @property {readonly string[]} slots
 * @property {(host: ScriptHost) => any} createNexus
 * @property {(config: Record<string, any>, host: ScriptHost) => any} createClient
 * @property {(proxy: any, method: string, args: readonly unknown[], options?: { pipe?: any, signal?: AbortSignal }) => Promise<unknown>} invokeByName
 * @property {(proxy: any, collection: string) => import('@dtronix/nexnet').NexusList<any>} list
 * @property {(name: string) => Method | null} method
 * @property {(name: string) => Collection | null} collection
 */

/**
 * The generated scaffolding modules a host runs with.
 * @typedef {object} Generated
 * @property {string} schemaHash `SCHEMA_HASH` of `scaffolding/index.js`.
 * @property {(clientNexus: string, serverNexus: string) => ScaffoldingExport | null} find
 * @property {(expression: string) => TypedCodec | null} forType `forType` of `scaffolding/test-codecs.js`.
 */

export {};
