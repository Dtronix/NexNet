/**
 * C# `System.Version`.
 * @module
 */

const INT32_MAX = 0x7fffffff;

/**
 * C# `System.Version`. `build` and `revision` are -1 when absent, as in C#.
 * Exported as both `NexusVersion` and `Version` (the name the G03 type mapper emits).
 */
export class NexusVersion {
  /**
   * @readonly
   * @type {number}
   */
  major;

  /**
   * @readonly
   * @type {number}
   */
  minor;

  /**
   * -1 when absent.
   * @readonly
   * @type {number}
   */
  build;

  /**
   * -1 when absent.
   * @readonly
   * @type {number}
   */
  revision;

  /**
   * @param {number} major
   * @param {number} minor
   * @param {number} [build] -1 when absent.
   * @param {number} [revision] -1 when absent; needs `build`.
   */
  constructor(major, minor, build = -1, revision = -1) {
    for (const [name, v, min] of /** @type {[string, number, number][]} */ ([['major', major, 0], ['minor', minor, 0], ['build', build, -1], ['revision', revision, -1]])) {
      if (!Number.isInteger(v) || v < min || v > INT32_MAX) throw new RangeError(`Version ${name} must be an integer from ${min} to ${INT32_MAX}; got ${v}`);
    }
    if (build < 0 && revision >= 0) throw new RangeError('a Version with a revision needs a build');
    this.major = major;
    this.minor = minor;
    this.build = build;
    this.revision = revision;
  }

  /**
   * Parses `major.minor[.build[.revision]]`: 2 to 4 non-negative int32 parts.
   * @param {string} text
   * @returns {NexusVersion}
   * @throws {SyntaxError} The text is not a version.
   */
  static parse(text) {
    const parts = String(text).split('.');
    if (parts.length < 2 || parts.length > 4 || !parts.every((p) => /^\d{1,10}$/.test(p) && Number(p) <= INT32_MAX)) {
      throw new SyntaxError(`not a Version: ${text}`);
    }
    const n = parts.map(Number);
    return new NexusVersion(n[0], n[1], n[2] ?? -1, n[3] ?? -1);
  }

  /** As C# `Version.ToString()`: 2 to 4 parts. @returns {string} */
  toString() {
    let s = `${this.major}.${this.minor}`;
    if (this.build >= 0) s += `.${this.build}`;
    if (this.revision >= 0) s += `.${this.revision}`;
    return s;
  }

  /** @param {NexusVersion} other @returns {boolean} */
  equals(other) {
    return other instanceof NexusVersion && other.major === this.major && other.minor === this.minor
      && other.build === this.build && other.revision === this.revision;
  }
}
