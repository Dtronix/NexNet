/**
 * Paths of the repository the hosts use.
 * @module
 */

import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** The repository root (holds `src/NexNet.slnx`), or `NEXNET_REPO_ROOT`. */
export const repoRoot = process.env.NEXNET_REPO_ROOT
  ? path.resolve(process.env.NEXNET_REPO_ROOT)
  : path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '..', '..', '..');

/** `clients/js/packages/interop-host-node`. */
export const hostPackageDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

/**
 * The JavaScript code the generator writes for `src/NexNet.TestNexus` (its NexNetTarget, J12), or
 * `NEXNET_INTEROP_JS_GENERATED`.
 */
export const generatedDir = process.env.NEXNET_INTEROP_JS_GENERATED
  ? path.resolve(process.env.NEXNET_INTEROP_JS_GENERATED)
  : path.join(repoRoot, 'artifacts', 'interop', 'js', 'generated');

/** Hand-written user formatters the generated code imports from its own directory (D12). */
export const formattersDir = path.join(hostPackageDir, 'src', 'formatters');

/** The command that writes the generated code. */
export const GENERATE_COMMAND = 'dotnet build src/NexNet.TestNexus';

/**
 * Parses `--name value` arguments.
 * @param {readonly string[]} argv
 * @returns {Record<string, string>}
 */
export function parseArgs(argv) {
  /** @type {Record<string, string>} */
  const args = {};
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg.startsWith('--') && i + 1 < argv.length) {
      args[arg.slice(2)] = argv[++i];
    }
  }
  return args;
}
