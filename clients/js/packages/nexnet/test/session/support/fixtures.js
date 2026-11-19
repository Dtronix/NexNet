/**
 * Loads the C#-recorded fixtures (fixtures/messages.jsonl, fixtures/frames.jsonl; see fixtures/recorder).
 * @module
 */

import { readFileSync } from 'node:fs';

/** @param {string} hex @returns {Uint8Array} */
export function fromHex(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  return out;
}

/** @param {Uint8Array} bytes @returns {string} */
export function toHex(bytes) {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/** @param {string} name @returns {any[]} */
function readJsonl(name) {
  return readFileSync(new URL(`../fixtures/${name}`, import.meta.url), 'utf8')
    .split(/\r?\n/)
    .filter((line) => line.trim() !== '')
    .map((line) => JSON.parse(line));
}

/**
 * @typedef {object} MessageVector
 * @property {string} name
 * @property {number} type
 * @property {Record<string, any>} fields
 * @property {string} body Hex.
 */

/**
 * @typedef {object} SessionRecording
 * @property {string} scenario
 * @property {number} clientNexusHash
 * @property {number} serverNexusHash
 * @property {number} clientDisconnectReason
 * @property {string} clientToServer Hex of every byte the C# client sent.
 * @property {string} serverToClient Hex of every byte the C# server sent.
 */

/** @type {MessageVector[]} */
export const messageVectors = readJsonl('messages.jsonl');

/** @type {Record<string, SessionRecording>} */
export const recordings = Object.fromEntries(readJsonl('frames.jsonl').map((r) => [r.scenario, r]));
