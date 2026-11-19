namespace NexNet.Generator.Targets.JavaScript;

/// <summary>
/// Emits <c>nexnet-support.js</c> (and its declarations): the structural equality and hash keys behind the
/// <c>equals</c> and <c>hashKey</c> methods of the generated classes.
/// </summary>
internal static class JsSupportEmitter
{
    public static IEnumerable<GeneratedFile> Emit(JsContext context)
    {
        yield return new GeneratedFile(JsSyntax.SupportModule + ".js",
            JsSyntax.Header + "\nimport * as " + JsSyntax.Rt + " from " + JsSyntax.String(context.RuntimeModule) + ";\n" + Lf(Js));
        yield return new GeneratedFile(JsSyntax.SupportModule + ".d.ts", JsSyntax.Header + "\n" + Lf(Dts));
    }

    // The raw strings take the line endings of this file's checkout; emitted files always use '\n'.
    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private const string Dts = """

        /**
         * Structural equality of generated values, as C# compares the values they map: generated objects by class and
         * members, arrays and typed arrays element-wise, Map and Set by content, the runtime value types by their
         * equals, NaN equal to NaN.
         */
        export declare function equalsValue(a: unknown, b: unknown): boolean;

        /** A string that is the same for values {@link equalsValue} finds equal: a Map or Set key for the value. */
        export declare function hashKeyOf(value: unknown): string;

        """;

    private const string Js = """

        /**
         * Structural equality of generated values, as C# compares the values they map: generated objects by class and
         * members, arrays and typed arrays element-wise, Map and Set by content, the runtime value types by their
         * equals, NaN equal to NaN. Other objects compare by identity.
         * @param {unknown} a
         * @param {unknown} b
         * @returns {boolean}
         */
        export function equalsValue(a, b) {
          if (a === b) return true;
          if (typeof a === 'number' && typeof b === 'number') return Number.isNaN(a) && Number.isNaN(b);
          if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') return false;
          const x = /** @type {any} */ (a);
          const y = /** @type {any} */ (b);
          if (typeof x.equals === 'function') return Boolean(x.equals(y));
          if (Array.isArray(x)) {
            if (!Array.isArray(y) || x.length !== y.length) return false;
            for (let i = 0; i < x.length; i++) if (!equalsValue(x[i], y[i])) return false;
            return true;
          }
          if (ArrayBuffer.isView(x)) {
            if (!ArrayBuffer.isView(y) || x.constructor !== y.constructor || x.length !== y.length) return false;
            for (let i = 0; i < x.length; i++) if (x[i] !== y[i] && !(Number.isNaN(x[i]) && Number.isNaN(y[i]))) return false;
            return true;
          }
          if (x instanceof Map) {
            if (!(y instanceof Map) || x.size !== y.size) return false;
            for (const [k, v] of x) {
              if (y.has(k)) {
                if (!equalsValue(v, y.get(k))) return false;
                continue;
              }
              let found = false;
              for (const [k2, v2] of y) {
                if (equalsValue(k, k2)) {
                  found = equalsValue(v, v2);
                  break;
                }
              }
              if (!found) return false;
            }
            return true;
          }
          if (x instanceof Set) {
            if (!(y instanceof Set) || x.size !== y.size) return false;
            for (const k of x) {
              if (y.has(k)) continue;
              let found = false;
              for (const k2 of y) {
                if (equalsValue(k, k2)) {
                  found = true;
                  break;
                }
              }
              if (!found) return false;
            }
            return true;
          }
          return false;
        }

        /** @type {WeakMap<object, number>} */
        const identities = new WeakMap();
        let nextIdentity = 0;

        /**
         * A string that is the same for values {@link equalsValue} finds equal: a Map or Set key for the value.
         * Objects compared by identity get a key unique to the object.
         * @param {unknown} value
         * @returns {string}
         */
        export function hashKeyOf(value) {
          switch (typeof value) {
            case 'undefined':
              return 'u';
            case 'boolean':
              return value ? 't' : 'f';
            case 'number':
              return 'n:' + (value === 0 ? '0' : String(value));
            case 'bigint':
              return 'b:' + String(value);
            case 'string':
              return 's:' + JSON.stringify(value);
            case 'function':
            case 'symbol':
              return 'x:' + String(value);
          }
          if (value === null) return 'null';
          const x = /** @type {any} */ (value);
          if (typeof x.hashKey === 'function') return String(x.hashKey());
          if (Array.isArray(x)) return '[' + x.map(hashKeyOf).join(',') + ']';
          if (ArrayBuffer.isView(x)) return x.constructor.name + '[' + Array.from(/** @type {any} */ (x), hashKeyOf).join(',') + ']';
          if (x instanceof Map) return 'M{' + Array.from(x, ([k, v]) => hashKeyOf(k) + '=' + hashKeyOf(v)).sort().join(',') + '}';
          if (x instanceof Set) return 'S{' + Array.from(x, hashKeyOf).sort().join(',') + '}';
          if (x instanceof $rt.NexusDecimal) {
            // Numeric equality: 1.0 and 1.00 are equal, and so are 0 and -0.
            if (x.magnitude === 0n) return 'd:0';
            let magnitude = x.magnitude;
            let scale = x.scale;
            while (scale > 0 && magnitude % 10n === 0n) {
              magnitude /= 10n;
              scale--;
            }
            return 'd:' + (x.negative ? '-' : '') + String(magnitude) + 'e-' + String(scale);
          }
          if (x instanceof $rt.NexusDateTime) return 'dt:' + String(x.ticks) + ':' + String(x.kind);
          if (x instanceof $rt.NexusDateTimeOffset) return 'dto:' + String(x.utcTicks);
          if (x instanceof $rt.NexusTimeSpan) return 'ts:' + String(x.ticks);
          if (x instanceof $rt.NexusDateOnly) return 'do:' + String(x.dayNumber);
          if (x instanceof $rt.NexusTimeOnly) return 'to:' + String(x.ticks);
          if (x instanceof $rt.NexusVersion) return 'v:' + String(x);
          let id = identities.get(x);
          if (id === undefined) {
            id = ++nextIdentity;
            identities.set(x, id);
          }
          return '#' + String(id);
        }
        """;
}
