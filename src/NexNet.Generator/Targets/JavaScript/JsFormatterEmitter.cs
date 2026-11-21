using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>
/// Emits <c>formatters.js</c> and <c>formatters.d.ts</c>: a formatter per generated type (<c>InteropItemFormatter</c>)
/// and a factory per generic type (<c>InteropBoxFormatter(f0)</c>), ports of the C# <c>EmitObject</c> and
/// <c>EmitUnion</c>; and <c>user-formatters.d.ts</c>, the interface the hand-written formatters of
/// <c>NexusTargetFormatter</c> mappings implement (D12).
/// </summary>
/// <remarks>
/// <para>
/// Objects are written as an array of <c>ArrayLength</c> elements with nil in the key gaps. Reading accepts fewer
/// elements (an older peer: the missing members are C# <c>default(T)</c>) and more (a newer peer: skipped). Struct
/// types and enums refuse null both ways. Unions are <c>[tag, value]</c>; the writer tests deeper cases first.
/// </para>
/// <para>
/// Module initialization: the type formatters are created first and only reference other formatters inside
/// <c>write</c>/<c>read</c>; the formatters composed from them (<c>$rt.arrayFormatter(InteropItemFormatter)</c>) are
/// constants declared after every type formatter. A generic factory composes its member formatters from its
/// arguments; a reference to itself is <c>$self</c> and a reference to another recursive generic type is created
/// lazily.
/// </para>
/// </remarks>
internal static class JsFormatterEmitter
{
    private const string Rt = JsSyntax.Rt + ".";

    /// <summary>A user formatter spec (<c>./money.js#MoneyFormatter</c>) and the .NET types it serializes.</summary>
    internal sealed record UserFormatterRef(string Spec, string Module, string? Export, string Alias, IReadOnlyList<string> ClrTypes);

    /// <summary>
    /// Parses a JavaScript <c>NexusTargetFormatter</c> name: <c>&lt;module&gt;#&lt;export&gt;</c> for a named export, or
    /// <c>&lt;module&gt;</c> for the default export. Null when the module is empty or the export is not an identifier.
    /// </summary>
    public static (string Module, string? Export)? ParseSpec(string spec)
    {
        var hash = spec.LastIndexOf('#');
        var module = (hash < 0 ? spec : spec.Substring(0, hash)).Trim();
        var export = hash < 0 ? null : spec.Substring(hash + 1).Trim();
        if (module.Length == 0 || (export != null && !JsSyntax.IsIdentifier(export)))
            return null;
        return (module, export);
    }

    /// <summary>The expression builder for code in the formatter module.</summary>
    public static JsFormatterExpressions Expressions(JsContext context, IReadOnlyList<UserFormatterRef> users)
        => new(context.Scope, "", spec => users.FirstOrDefault(u => u.Spec == spec)?.Alias
                                          ?? throw new InvalidOperationException("No user formatter " + spec + "."));

    public static IEnumerable<GeneratedFile> Emit(JsContext context, IReadOnlyList<UserFormatterRef> users)
    {
        yield return new GeneratedFile(JsSyntax.FormattersModule + ".js", FormattersJs(context, users));
        yield return new GeneratedFile(JsSyntax.FormattersModule + ".d.ts", FormattersDts(context));
        if (users.Count > 0)
            yield return new GeneratedFile(JsSyntax.UserFormattersModule + ".d.ts", UserFormattersDts(context, users));
    }

    private static bool HasFormatter(TypeDeclIr decl) => decl is not ObjectDeclIr { IsAbstract: true };

    // ------------------------------------------------------------------ formatters.js

    private static string FormattersJs(JsContext context, IReadOnlyList<UserFormatterRef> users)
    {
        var expressions = Expressions(context, users);
        var composites = new Hoist("$c");
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var body = new CodeWriter("  ");

        foreach (var decl in context.Decls.Where(HasFormatter))
        {
            body.Line();
            switch (decl)
            {
                case ObjectDeclIr obj:
                    WriteObject(body, context, obj, expressions, composites, used);
                    break;
                case UnionDeclIr union:
                    WriteUnion(body, context, union, expressions, composites, used);
                    break;
                case EnumDeclIr e:
                    WriteEnum(body, context, e);
                    break;
            }
        }

        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line("import * as " + JsSyntax.Rt + " from " + JsSyntax.String(context.RuntimeModule) + ";");
        foreach (var name in used)
            w.Line("import { " + name + " as $" + name + " } from './" + name + ".js';");
        foreach (var user in users)
        {
            w.Line(user.Export == null
                ? "import " + user.Alias + " from " + JsSyntax.String(user.Module) + ";"
                : "import { " + user.Export + " as " + user.Alias + " } from " + JsSyntax.String(user.Module) + ";");
        }

        if (expressions.UsesLazy)
        {
            w.Line();
            w.Lines(LazyHelper);
        }

        if (expressions.UsesSeq)
        {
            w.Line();
            w.Lines(SeqHelper);
        }

        w.Lines(body.ToString().TrimEnd('\n'));
        if (composites.Entries.Count > 0)
        {
            w.Line();
            w.Line("// Formatters composed from the ones above.");
            foreach (var (expression, name) in composites.Entries)
                w.Line("const " + name + " = " + expression + ";");
        }

        return w.ToString();
    }

    /// <summary>Composed formatters hoisted into constants, deduplicated by expression.</summary>
    private sealed class Hoist
    {
        private readonly string _prefix;
        private readonly Dictionary<string, string> _byExpression = new(StringComparer.Ordinal);

        public Hoist(string prefix) => _prefix = prefix;

        public List<(string Expression, string Name)> Entries { get; } = new();

        public string For(JsFormatterExpressions expressions, TypeRef type)
        {
            var expression = expressions.For(type);
            if (JsFormatterExpressions.IsSimple(expression))
                return expression;
            if (!_byExpression.TryGetValue(expression, out var name))
            {
                name = _prefix + JsSyntax.Int(Entries.Count);
                Entries.Add((expression, name));
                _byExpression[expression] = name;
            }

            return name;
        }
    }

    /// <summary>The documentation of a type's formatter.</summary>
    private static void FormatterDoc(CodeWriter w, JsContext context, TypeDeclIr decl, bool js)
    {
        var name = context.Name(decl);
        var typeParams = JsContext.TypeParams(decl);
        var what = "C# " + JsSyntax.Code(JsContext.ClrName(decl));
        if (typeParams.Length == 0)
        {
            JsSyntax.Doc(w, "The formatter of " + JsSyntax.Code(name) + " (" + what + ")."
                            + (JsContext.IsValueType(decl) ? " A value type: null is refused." : ""));
            return;
        }

        var tags = js
            ? string.Join("\n", typeParams.Select((p, i) => "@param {import('" + context.RuntimeModule + "').Formatter<any>} "
                                                                + JsFormatterExpressions.TypeParamFormatter(i) + " The formatter of " + JsSyntax.Code(p) + "."))
            : string.Join("\n", typeParams.Select((p, i) => "@param " + JsFormatterExpressions.TypeParamFormatter(i) + " The formatter of " + JsSyntax.Code(p) + "."));
        JsSyntax.Doc(w, "Creates the formatter of " + JsSyntax.Code(name) + " (" + what + ") for the type arguments.", tags);
    }

    /// <summary>Writes a type formatter: a frozen constant, or a factory for a generic type.</summary>
    private static void WriteFormatter(
        CodeWriter w,
        JsContext context,
        TypeDeclIr decl,
        JsFormatterExpressions expressions,
        Hoist composites,
        Action<CodeWriter, Func<TypeRef, string>> methods)
    {
        var name = JsFormatterExpressions.FormatterName(context.Name(decl));
        var typeParams = JsContext.TypeParams(decl);
        FormatterDoc(w, context, decl, js: true);
        if (typeParams.Length == 0)
        {
            w.Line("export const " + name + " = Object.freeze({").Indent();
            methods(w, t => composites.For(expressions, t));
            w.Unindent().Line("});");
            return;
        }

        var local = expressions.ForGeneric(decl.Id, typeParams.Length);
        var hoist = new Hoist("$l");
        var parameters = string.Join(", ", typeParams.Select((_, i) => JsFormatterExpressions.TypeParamFormatter(i)));
        using (w.Block("export function " + name + "(" + parameters + ")"))
        {
            w.Line("const " + JsFormatterExpressions.Self + " = Object.freeze({").Indent();
            methods(w, t => hoist.For(local, t));
            w.Unindent().Line("});");
            foreach (var (expression, localName) in hoist.Entries)
                w.Line("const " + localName + " = " + expression + ";");
            w.Line("return " + JsFormatterExpressions.Self + ";");
        }
    }

    private static void WriteObject(CodeWriter w, JsContext context, ObjectDeclIr obj, JsFormatterExpressions expressions, Hoist composites, SortedSet<string> used)
    {
        var name = context.Name(obj);
        var scope = context.Scope.WithTypeParams(obj.TypeParams.ToArray());
        var members = JsContext.Members(obj);
        used.Add(name);

        string DeclName(TypeDeclIr d)
        {
            used.Add(context.Name(d));
            return "$" + context.Name(d);
        }

        WriteFormatter(w, context, obj, expressions, composites, (b, formatter) =>
        {
            using (b.Block("write(w, v)", "{", "},"))
            {
                using (b.Block("if (v === null || v === undefined)"))
                {
                    if (obj.IsStruct)
                    {
                        b.Line("throw new TypeError(" + JsSyntax.String(name + " is a value type and cannot be null.") + ");");
                    }
                    else
                    {
                        b.Line("w.writeNil();");
                        b.Line("return;");
                    }
                }

                b.Line("w.writeArrayHeader(" + JsSyntax.Int(obj.ArrayLength) + ");");
                var byKey = new Dictionary<int, MemberIr>();
                foreach (var m in members)
                {
                    if (!byKey.ContainsKey(m.Key))
                        byKey[m.Key] = m;
                }

                for (var key = 0; key < obj.ArrayLength; key++)
                {
                    b.Line(byKey.TryGetValue(key, out var m)
                        ? formatter(m.Type) + ".write(w, v." + context.Target.Names.Property(m.Name) + ");"
                        : "w.writeNil();");
                }
            }

            using (b.Block("read(r)", "{", "},"))
            {
                if (obj.IsStruct)
                {
                    using (b.Block("if (r.peekCode() === 0xc0)"))
                        b.Line("throw new " + Rt + "NexusSerializationError('type', " + JsSyntax.String("nil is not a value of the value type " + name + ".") + ");");
                }
                else
                {
                    using (b.Block("if (r.tryReadNil())"))
                        b.Line("return null;");
                }

                b.Line("r.enter();");
                b.Line("const n = r.readArrayHeader();");
                for (var i = 0; i < members.Count; i++)
                    b.Line("let m" + JsSyntax.Int(i) + " = " + JsValues.Default(members[i].Type, scope, DeclName) + ";");

                using (b.Block("for (let i = 0; i < n; i++)"))
                {
                    if (members.Count == 0)
                    {
                        b.Line("r.skip();");
                    }
                    else
                    {
                        using (b.Block("switch (i)"))
                        {
                            var seen = new HashSet<int>();
                            for (var i = 0; i < members.Count; i++)
                            {
                                if (!seen.Add(members[i].Key))
                                    continue;
                                b.Line("case " + JsSyntax.Int(members[i].Key) + ":");
                                b.Indent();
                                b.Line("m" + JsSyntax.Int(i) + " = " + formatter(members[i].Type) + ".read(r);");
                                b.Line("break;");
                                b.Unindent();
                            }

                            b.Line("default:");
                            b.Indent().Line("r.skip();").Line("break;").Unindent();
                        }
                    }
                }

                b.Line("r.exit();");
                var init = string.Join(", ", members.Select((m, i) => context.Target.Names.Property(m.Name) + ": m" + JsSyntax.Int(i)));
                b.Line("return new $" + name + "(" + (members.Count == 0 ? "" : "{ " + init + " }") + ");");
            }
        });
    }

    private static void WriteUnion(CodeWriter w, JsContext context, UnionDeclIr union, JsFormatterExpressions expressions, Hoist composites, SortedSet<string> used)
    {
        var name = context.Name(union);
        var cases = union.Cases
            .Select(c => (Case: c, Decl: context.Scope.Find(c.CaseDeclId)))
            .Where(c => c.Decl != null)
            .ToArray();

        // A generic case takes the union's type parameters, in order.
        TypeRef CaseRef(TypeDeclIr decl)
        {
            var count = JsContext.TypeParams(decl).Length;
            return new TypeRef(TypeRefKind.Decl)
            {
                DeclId = decl.Id,
                Args = new(Enumerable.Range(0, count).Select(i => new TypeRef(TypeRefKind.TypeParam) { Ordinal = i }).ToArray()),
            };
        }

        WriteFormatter(w, context, union, expressions, composites, (b, formatter) =>
        {
            using (b.Block("write(w, v)", "{", "},"))
            {
                using (b.Block("if (v === null || v === undefined)"))
                {
                    b.Line("w.writeNil();");
                    b.Line("return;");
                }

                // Deeper cases first, so a subclass case is not shadowed by its base case (stable for ties).
                foreach (var (unionCase, decl) in cases.OrderByDescending(c => c.Case.InheritanceDepth))
                {
                    var caseName = context.Name(decl!);
                    used.Add(caseName);
                    using (b.Block("if (v instanceof $" + caseName + ")"))
                    {
                        b.Line("w.writeArrayHeader(2);");
                        b.Line("w.writeInt(" + JsSyntax.Int(unionCase.Tag) + ");");
                        b.Line(formatter(CaseRef(decl!)) + ".write(w, v);");
                        b.Line("return;");
                    }
                }

                b.Line("throw new TypeError(" + JsSyntax.String("The value is not a case of the union " + name + ".") + ");");
            }

            using (b.Block("read(r)", "{", "},"))
            {
                using (b.Block("if (r.tryReadNil())"))
                    b.Line("return null;");
                b.Line("r.enter();");
                b.Line("r.readArrayHeader(2);");
                b.Line("const tag = r.readUInt16();");
                b.Line("let v;");
                using (b.Block("switch (tag)"))
                {
                    foreach (var (unionCase, decl) in cases)
                    {
                        b.Line("case " + JsSyntax.Int(unionCase.Tag) + ":");
                        b.Indent();
                        b.Line("v = " + formatter(CaseRef(decl!)) + ".read(r);");
                        b.Line("break;");
                        b.Unindent();
                    }

                    b.Line("default:");
                    b.Indent();
                    b.Line("throw new " + Rt + "NexusSerializationError('value', 'Unknown tag ' + tag + " + JsSyntax.String(" of the union " + name + ".") + ");");
                    b.Unindent();
                }

                b.Line("r.exit();");
                b.Line("return v;");
            }
        });
    }

    private static void WriteEnum(CodeWriter w, JsContext context, EnumDeclIr e)
    {
        var name = context.Name(e);
        var underlying = Rt + JsFormatterExpressions.PrimFormatter(e.Underlying);
        FormatterDoc(w, context, e, js: true);
        w.Line("export const " + JsFormatterExpressions.FormatterName(name) + " = Object.freeze({").Indent();
        using (w.Block("write(w, v)", "{", "},"))
        {
            using (w.Block("if (v === null || v === undefined)"))
                w.Line("throw new TypeError(" + JsSyntax.String(name + " is a value type and cannot be null.") + ");");
            w.Line(underlying + ".write(w, v);");
        }

        using (w.Block("read(r)", "{", "},"))
            w.Line("return " + underlying + ".read(r);");
        w.Unindent().Line("});");
    }

    // ------------------------------------------------------------------ formatters.d.ts

    private static string FormattersDts(JsContext context)
    {
        var imports = new SortedSet<string>(StringComparer.Ordinal);
        var body = new CodeWriter("  ");
        foreach (var decl in context.Decls.Where(HasFormatter))
        {
            var name = context.Name(decl);
            imports.Add(name);
            var typeParams = JsContext.TypeParams(decl);
            var generic = typeParams.Length == 0 ? "" : "<" + string.Join(", ", typeParams) + ">";
            var type = name + generic + (JsContext.IsValueType(decl) ? "" : " | null");
            body.Line();
            FormatterDoc(body, context, decl, js: false);
            if (typeParams.Length == 0)
            {
                body.Line("export declare const " + JsFormatterExpressions.FormatterName(name) + ": Formatter<" + type + ">;");
            }
            else
            {
                var parameters = string.Join(", ", typeParams.Select((p, i) => JsFormatterExpressions.TypeParamFormatter(i) + ": Formatter<" + p + ">"));
                body.Line("export declare function " + JsFormatterExpressions.FormatterName(name) + generic + "(" + parameters + "): Formatter<" + type + ">;");
            }
        }

        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line("import type { Formatter } from " + JsSyntax.String(context.RuntimeModule) + ";");
        foreach (var name in imports)
            w.Line("import type { " + name + " } from './" + name + ".js';");
        if (imports.Count == 0)
            w.Line("export {};");
        w.Lines(body.ToString().TrimEnd('\n'));
        return w.ToString();
    }

    // ------------------------------------------------------------------ user-formatters.d.ts

    /// <summary>The native formatters (D12) of every external type the JavaScript code serializes, by spec.</summary>
    public static IReadOnlyList<UserFormatterRef> UserFormatters(NexusIr ir, IReadOnlyList<TypeDeclIr> decls)
    {
        var found = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        void Visit(TypeRef? type)
        {
            if (type == null)
                return;
            if (type.Kind == TypeRefKind.External
                && type.NativeFormatters.FirstOrDefault(f => f.Target == (int)NexusTargets.JavaScript) is { } formatter)
            {
                if (!found.TryGetValue(formatter.Formatter, out var clrTypes))
                    found[formatter.Formatter] = clrTypes = new SortedSet<string>(StringComparer.Ordinal);
                clrTypes.Add(type.Clr ?? "?");
            }

            foreach (var arg in type.Args)
                Visit(arg);
        }

        foreach (var decl in decls.OfType<ObjectDeclIr>())
        {
            foreach (var member in decl.Members)
                Visit(member.Type);
        }

        foreach (var export in ir.Exports.Where(e => (e.Targets & (int)NexusTargets.JavaScript) != 0))
        {
            foreach (var api in new[] { export.ServerApi, export.ClientApi })
            {
                foreach (var method in api.Methods)
                {
                    Visit(method.ReturnType);
                    Visit(method.PipeType);
                    foreach (var param in method.Params)
                        Visit(param.Type);
                }

                foreach (var collection in api.Collections)
                    Visit(collection.Item);
            }
        }

        var result = new List<UserFormatterRef>();
        foreach (var entry in found)
        {
            // Invalid specs are NEXNET048 (JavaScriptTarget.Validate); nothing is emitted for them.
            if (ParseSpec(entry.Key) is not { } parsed)
                continue;
            result.Add(new UserFormatterRef(entry.Key, parsed.Module, parsed.Export, "$user" + JsSyntax.Int(result.Count), entry.Value.ToArray()));
        }

        return result;
    }

    private static string UserFormattersDts(JsContext context, IReadOnlyList<UserFormatterRef> users)
    {
        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line("import type { Formatter } from " + JsSyntax.String(context.RuntimeModule) + ";");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in users)
        {
            var name = user.Export ?? "UserFormatter";
            if (!names.Add(name))
            {
                var i = 2;
                while (!names.Add(name + JsSyntax.Int(i)))
                    i++;
                name += JsSyntax.Int(i);
            }

            w.Line();
            JsSyntax.Doc(w,
                "The formatter " + (user.Export == null ? "exported by default" : JsSyntax.Code(user.Export)) + " of "
                + JsSyntax.Code(user.Module) + ", declared with " + JsSyntax.Code("[assembly: NexusTargetFormatter<T>(NexusTargets.JavaScript, \"" + user.Spec + "\")]")
                + " for " + string.Join(", ", user.ClrTypes.Select(JsSyntax.Code)) + ".",
                "It must write exactly the bytes of the C# formatter and read them back. The generated formatters import it "
                + "from that module (a relative path is relative to the generated files); generated members of these types are typed "
                + JsSyntax.Code("unknown") + ".");
            w.Line("export interface " + name + " extends Formatter<any> {}");
        }

        return w.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private const string LazyHelper = """
        /**
         * A formatter created on first use: breaks the construction cycle of mutually recursive generic types.
         * @param {() => import('@dtronix/nexnet').Formatter<any>} create
         */
        function $lazy(create) {
          /** @type {import('@dtronix/nexnet').Formatter<any> | null} */
          let f = null;
          return Object.freeze({
            write(w, v) {
              (f ??= create()).write(w, v);
            },
            read(r) {
              return (f ??= create()).read(r);
            },
          });
        }
        """;

    // C# resolves the formatter of T[] and List<T> when T is bound: ext 78 for the primitive element kinds, bin for
    // byte[] (List<byte> stays an array). The JS member is an array; this converts it to and from the typed array.
    private const string SeqHelper = """
        /**
         * The formatter of a C# T[] (isArray) or List<T> of a type parameter: as C# does once T is bound, an ext 78
         * primitive array for the primitive element formatters, bin for byte[], otherwise an array of elements. The
         * value is a JS array either way.
         * @param {import('@dtronix/nexnet').Formatter<any>} element
         * @param {boolean} isArray
         */
        function $seq(element, isArray) {
          /** @type {[import('@dtronix/nexnet').Formatter<any>, import('@dtronix/nexnet').Formatter<any>, any, string][]} */
          const packed = [
            [$rt.sbyteFormatter, $rt.int8ArrayFormatter, Int8Array, 'int'],
            [$rt.int16Formatter, $rt.int16ArrayFormatter, Int16Array, 'int'],
            [$rt.uint16Formatter, $rt.uint16ArrayFormatter, Uint16Array, 'int'],
            [$rt.int32Formatter, $rt.int32ArrayFormatter, Int32Array, 'int'],
            [$rt.uint32Formatter, $rt.uint32ArrayFormatter, Uint32Array, 'int'],
            [$rt.int64Formatter, $rt.int64ArrayFormatter, BigInt64Array, 'int'],
            [$rt.uint64Formatter, $rt.uint64ArrayFormatter, BigUint64Array, 'int'],
            [$rt.int64NumberFormatter, $rt.int64ArrayFormatter, BigInt64Array, 'number'],
            [$rt.uint64NumberFormatter, $rt.uint64ArrayFormatter, BigUint64Array, 'number'],
            [$rt.float32Formatter, $rt.float32ArrayFormatter, Float32Array, 'float'],
            [$rt.float64Formatter, $rt.float64ArrayFormatter, Float64Array, 'float'],
            [$rt.halfFormatter, $rt.halfArrayFormatter, Float32Array, 'float'],
            [$rt.charFormatter, $rt.charArrayFormatter, Uint16Array, 'char'],
          ];
          if (isArray) packed.push([$rt.byteFormatter, $rt.binFormatter, Uint8Array, 'int']);
          const entry = packed.find((p) => p[0] === element);
          if (entry === undefined) return $rt.arrayFormatter(element);
          const [, formatter, ArrayType, kind] = entry;
          return Object.freeze({
            /** @param {import('@dtronix/nexnet').MsgPackWriter} w @param {any} v */
            write(w, v) {
              if (v === null || v === undefined) {
                w.writeNil();
                return;
              }
              const items = Array.from(v);
              if (kind === 'char') {
                formatter.write(w, Uint16Array.from(items, (c) => {
                  if (typeof c !== 'string' || c.length !== 1) throw new TypeError('char must be a string of one UTF-16 code unit');
                  return c.charCodeAt(0);
                }));
                return;
              }
              if (kind === 'number') {
                // 64-bit elements in the checked-number mapping (D13): safe integers, written as the bigint array.
                formatter.write(w, ArrayType.from(items, (x, i) => {
                  if (typeof x !== 'number' || !Number.isSafeInteger(x) || (ArrayType === BigUint64Array && x < 0)) {
                    throw new RangeError('element ' + i + ' (' + String(x) + ') is not a safe integer of the element type');
                  }
                  return BigInt(x);
                }));
                return;
              }
              const array = ArrayType.from(items);
              if (kind === 'int') {
                for (let i = 0; i < items.length; i++) {
                  if (array[i] !== items[i]) throw new RangeError('element ' + i + ' (' + String(items[i]) + ') is not a value of the element type');
                }
              }
              formatter.write(w, array);
            },
            /** @param {import('@dtronix/nexnet').MsgPackReader} r */
            read(r) {
              const array = formatter.read(r);
              if (array === null) return null;
              if (kind === 'number') {
                return Array.from(array, (b) => {
                  if (b < -9007199254740991n || b > 9007199254740991n) {
                    throw new $rt.NexusSerializationError('range', 'the integer ' + String(b) + ' is not a safe integer');
                  }
                  return Number(b);
                });
              }
              return kind === 'char' ? Array.from(array, (c) => String.fromCharCode(c)) : Array.from(array);
            },
          });
        }
        """;
}
