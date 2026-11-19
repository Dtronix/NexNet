/**
 * Module resolution hooks (`module.register`) for the generated code in `artifacts/interop/js/generated`, outside the
 * npm workspace: its `@dtronix/nexnet` imports resolve from this package (the workspace's runtime), and imports of
 * hand-written user formatters (D12) it does not have resolve to `src/formatters`.
 * @module
 */

import { existsSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

/** @type {{ generated: string, formatters: string, parent: string } | null} */
let config = null;

/**
 * @param {{ generated: string, formatters: string, parent: string }} data URLs of the generated directory (ending in
 *   '/'), the formatters directory (ending in '/') and a module of this package to resolve the runtime from.
 */
export function initialize(data) {
  config = data;
}

/**
 * @param {string} specifier
 * @param {{ parentURL?: string, conditions?: string[] }} context
 * @param {(specifier: string, context?: object) => Promise<{ url: string }>} nextResolve
 * @returns {Promise<{ url: string, shortCircuit?: boolean }>}
 */
export async function resolve(specifier, context, nextResolve) {
  const parent = context.parentURL ?? '';
  if (config !== null && parent.startsWith(config.generated)) {
    if (specifier === '@dtronix/nexnet' || specifier.startsWith('@dtronix/nexnet/')) {
      return nextResolve(specifier, { ...context, parentURL: config.parent });
    }
    if (specifier.startsWith('./') || specifier.startsWith('../')) {
      const target = new URL(specifier, parent);
      if (target.href.startsWith(config.generated) && !existsSync(fileURLToPath(target))) {
        const replacement = pathToFileURL(path.join(fileURLToPath(config.formatters), path.basename(fileURLToPath(target))));
        if (existsSync(fileURLToPath(replacement))) return { url: replacement.href, shortCircuit: true };
      }
    }
  }
  return nextResolve(specifier, context);
}
