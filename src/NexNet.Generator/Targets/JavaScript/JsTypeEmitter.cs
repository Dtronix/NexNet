using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>
/// Emits one module and its declarations per declared type: <c>&lt;Name&gt;.js</c> and <c>&lt;Name&gt;.d.ts</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>[NexusObject] classes and structs are classes with a <c>constructor(init)</c>: a member missing from
/// <c>init</c> starts at its C# initial value (the initializer literal, else <c>default(T)</c>), so a new instance
/// writes the bytes of a new C# instance. They have a structural <c>equals(other)</c> (the runtime's list equality
/// uses it for <c>remove(item)</c>) and a <c>hashKey()</c>: a string equal for equal values, for Map and Set keys.</item>
/// <item>Enums are a frozen object of constants and a TypeScript type of their values (a literal union; any number,
/// or bigint for 64-bit enums, for [Flags]).</item>
/// <item>Unions over an abstract class are an abstract class their cases extend, with <c>Name.Case</c> the union of
/// the case types. Unions over an interface are a marker whose <c>instanceof</c> matches the cases, and a type alias.</item>
/// </list>
/// Modules import each other only for a base class, the cases of an interface union, and the enums and structs
/// initial values use; these never form a cycle.
/// </remarks>
internal static class JsTypeEmitter
{
    private const string Rt = JsSyntax.Rt;

    /// <summary>The import names of the support module helpers.</summary>
    private const string SupportImport = "import { equalsValue as $eq, hashKeyOf as $key } from './" + JsSyntax.SupportModule + ".js';";

    public static IEnumerable<GeneratedFile> Emit(JsContext context, TypeDeclIr decl)
    {
        var name = context.Name(decl);
        switch (decl)
        {
            case ObjectDeclIr obj:
                yield return new GeneratedFile(name + ".js", ObjectJs(context, obj));
                yield return new GeneratedFile(name + ".d.ts", ObjectDts(context, obj));
                break;
            case UnionDeclIr union:
                yield return new GeneratedFile(name + ".js", UnionJs(context, union));
                yield return new GeneratedFile(name + ".d.ts", UnionDts(context, union));
                break;
            case EnumDeclIr e:
                yield return new GeneratedFile(name + ".js", EnumJs(context, e));
                yield return new GeneratedFile(name + ".d.ts", EnumDts(context, e));
                break;
        }
    }

    // ------------------------------------------------------------------ objects

    private static string ObjectJs(JsContext context, ObjectDeclIr obj)
    {
        var name = context.Name(obj);
        var scope = context.Scope.WithTypeParams(obj.TypeParams.ToArray());
        var baseClass = context.BaseClass(obj);
        var own = context.OwnMembers(obj);
        var all = JsContext.Members(obj);

        var imports = new SortedSet<string>(StringComparer.Ordinal);
        var usesRuntime = false;
        string DeclName(TypeDeclIr d)
        {
            if (d.Id != obj.Id)
                imports.Add(context.Name(d));
            return context.Name(d);
        }

        var initial = new List<(MemberIr Member, string Property, string Value)>();
        foreach (var member in own)
        {
            var literal = JsValues.Literal(member.DefaultLiteral, member.Type, scope, DeclName, context.Target.Names);
            var value = literal ?? JsValues.Default(member.Type, scope, DeclName);
            usesRuntime |= value.Contains(Rt + ".");
            initial.Add((member, context.Target.Names.Property(member.Name), value));
        }

        if (baseClass != null)
            imports.Add(context.Name(baseClass));

        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        if (usesRuntime)
            w.Line("import * as " + Rt + " from " + JsSyntax.String(context.RuntimeModule) + ";");
        if (!obj.IsAbstract)
            w.Line(SupportImport);
        foreach (var import in imports)
            w.Line("import { " + import + " } from './" + import + ".js';");
        w.Line();

        JsSyntax.Doc(w, obj.Docs, "C# " + JsSyntax.Code(JsContext.ClrName(obj)) + " (" + Kind(obj) + ")." + Generic(obj));
        using (w.Block("export class " + name + (baseClass == null ? "" : " extends " + context.Name(baseClass))))
        {
            JsSyntax.Doc(w, "Creates the " + (obj.IsStruct ? "value" : "object") + ". A member missing from " + JsSyntax.Code("init") + " starts at its C# initial value.",
                "@param {" + Init(name) + "} [init] Member values.");
            using (w.Block("constructor(init)"))
            {
                if (obj.IsAbstract)
                {
                    using (w.Block("if (new.target === " + name + ")"))
                        w.Line("throw new TypeError(" + JsSyntax.String(name + " is abstract.") + ");");
                }

                if (baseClass != null)
                    w.Line("super(init);");
                foreach (var (member, property, value) in initial)
                {
                    JsSyntax.Doc(w, member.Docs);
                    w.Line("this." + property + " = init?." + property + " === undefined ? " + value + " : init." + property + ";");
                }
            }

            if (!obj.IsAbstract)
            {
                w.Line();
                JsSyntax.Doc(w, "Structural equality: an instance of the same class with equal members.",
                    "@param {unknown} other\n@returns {boolean}");
                using (w.Block("equals(other)"))
                {
                    var test = "other instanceof " + name + " && other.constructor === this.constructor";
                    if (all.Count == 0)
                    {
                        w.Line("return " + test + ";");
                    }
                    else
                    {
                        w.Line("return " + test);
                        w.Indent();
                        for (var i = 0; i < all.Count; i++)
                        {
                            var p = context.Target.Names.Property(all[i].Name);
                            w.Line("&& $eq(this." + p + ", other." + p + ")" + (i == all.Count - 1 ? ";" : ""));
                        }

                        w.Unindent();
                    }
                }

                w.Line();
                JsSyntax.Doc(w, "A string that is the same for equal values: use it as a Map or Set key.", "@returns {string}");
                using (w.Block("hashKey()"))
                {
                    var parts = new[] { JsSyntax.String(name) }.Concat(all.Select(m => "this." + context.Target.Names.Property(m.Name)));
                    w.Line("return $key([" + string.Join(", ", parts) + "]);");
                }
            }
        }

        return w.ToString();
    }

    private static string ObjectDts(JsContext context, ObjectDeclIr obj)
    {
        var name = context.Name(obj);
        var scope = context.Scope.WithTypeParams(obj.TypeParams.ToArray());
        var baseClass = context.BaseClass(obj);
        var own = context.OwnMembers(obj);
        var typeParams = obj.TypeParams.Length == 0 ? "" : "<" + string.Join(", ", obj.TypeParams) + ">";

        var types = new DtsImports(context, obj.Id);
        foreach (var member in own)
            types.Add(member.Type);

        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        types.Write(w, baseClass);
        w.Line();

        JsSyntax.Doc(w, obj.Docs, "C# " + JsSyntax.Code(JsContext.ClrName(obj)) + " (" + Kind(obj) + ")." + Generic(obj));
        var head = "export declare " + (obj.IsAbstract ? "abstract " : "") + "class " + name + typeParams
                   + (baseClass == null ? "" : " extends " + context.Name(baseClass));
        using (w.Block(head))
        {
            JsSyntax.Doc(w, "Creates the " + (obj.IsStruct ? "value" : "object") + ". A member missing from " + JsSyntax.Code("init") + " starts at its C# initial value.");
            w.Line((obj.IsAbstract ? "protected " : "") + "constructor(init?: " + name + ".Init" + typeParams + ");");
            foreach (var member in own)
            {
                JsSyntax.Doc(w, member.Docs);
                w.Line(context.Target.Names.Property(member.Name) + ": " + context.TsType(member.Type, scope) + ";");
            }

            if (!obj.IsAbstract)
            {
                JsSyntax.Doc(w, "Structural equality: an instance of the same class with equal members.");
                w.Line("equals(other: unknown): boolean;");
                JsSyntax.Doc(w, "A string that is the same for equal values: use it as a Map or Set key.");
                w.Line("hashKey(): string;");
            }
        }

        w.Line();
        using (w.Block("export declare namespace " + name))
        {
            JsSyntax.Doc(w, "Member values for the constructor of " + JsSyntax.Code(name) + ".");
            var extends = baseClass is ObjectDeclIr ? " extends " + context.Name(baseClass) + ".Init" : "";
            using (w.Block("interface Init" + typeParams + extends))
            {
                foreach (var member in own)
                    w.Line(context.Target.Names.Property(member.Name) + "?: " + context.TsType(member.Type, scope) + ";");
            }
        }

        return w.ToString();
    }

    private static string Kind(ObjectDeclIr obj)
        => obj.IsStruct
            ? obj.IsRecord ? "record struct" : "struct"
            : (obj.IsAbstract ? "abstract " : "") + (obj.IsRecord ? "record" : "class");

    private static string Generic(TypeDeclIr decl)
    {
        var typeParams = JsContext.TypeParams(decl);
        return typeParams.Length == 0
            ? ""
            : " Its formatter is created for the type arguments: " + JsSyntax.Code(JsFormatterExpressions.FormatterName(JavaScriptTarget.Instance.TypeName(decl))
                + "(" + string.Join(", ", typeParams.Select((_, i) => JsFormatterExpressions.TypeParamFormatter(i))) + ")") + ".";
    }

    private static string Init(string name) => name + ".Init";

    // ------------------------------------------------------------------ unions

    private static IEnumerable<(UnionCaseIr Case, TypeDeclIr Decl)> Cases(JsContext context, UnionDeclIr union)
        => union.Cases
            .Select(c => (Case: c, Decl: context.Scope.Find(c.CaseDeclId)))
            .Where(c => c.Decl != null)
            .Select(c => (c.Case, c.Decl!));

    /// <summary>The TypeScript type of a case: generic cases take the union's type parameters when the counts match.</summary>
    private static string CaseType(JsContext context, UnionDeclIr union, TypeDeclIr decl)
    {
        var count = JsContext.TypeParams(decl).Length;
        return count == 0
            ? context.Name(decl)
            : context.Name(decl) + "<" + string.Join(", ", Enumerable.Range(0, count).Select(i => i < union.TypeParams.Length ? union.TypeParams[i] : "unknown")) + ">";
    }

    private static string CaseList(JsContext context, UnionDeclIr union)
        => string.Join(", ", Cases(context, union).Select(c => JsSyntax.Code(context.Name(c.Decl)) + " (" + JsSyntax.Int(c.Case.Tag) + ")"));

    private static string UnionJs(JsContext context, UnionDeclIr union)
    {
        var name = context.Name(union);
        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        var cases = Cases(context, union).ToArray();
        if (union.IsInterface)
        {
            foreach (var import in cases.Select(c => context.Name(c.Decl)).Distinct().OrderBy(n => n, StringComparer.Ordinal))
                w.Line("import { " + import + " } from './" + import + ".js';");
        }

        w.Line();
        var doc = "C# " + JsSyntax.Code(JsContext.ClrName(union)) + ": a union over " + (union.IsInterface ? "an interface" : "an abstract class")
                  + ", written as " + JsSyntax.Code("[tag, value]") + ". Cases: " + CaseList(context, union) + "." + Generic(union);
        if (union.IsInterface)
        {
            JsSyntax.Doc(w, union.Docs, doc, JsSyntax.Code("value instanceof " + name) + " is true for an instance of a case.");
            w.Line("export const " + name + " = Object.freeze({").Indent();
            JsSyntax.Doc(w, "@param {unknown} value\n@returns {boolean}");
            using (w.Block("[Symbol.hasInstance](value)"))
            {
                w.Line(cases.Length == 0
                    ? "return false;"
                    : "return " + string.Join(" || ", cases.Select(c => "value instanceof " + context.Name(c.Decl))) + ";");
            }

            w.Unindent().Line("});");
        }
        else
        {
            JsSyntax.Doc(w, union.Docs, doc, "Abstract: create one of its cases.");
            using (w.Block("export class " + name))
            {
                JsSyntax.Doc(w, "@param {unknown} [init] The member values of the case.");
                using (w.Block("constructor(init)"))
                {
                    w.Line("void init;");
                    using (w.Block("if (new.target === " + name + ")"))
                        w.Line("throw new TypeError(" + JsSyntax.String(name + " is abstract: create one of its cases.") + ");");
                }
            }
        }

        return w.ToString();
    }

    private static string UnionDts(JsContext context, UnionDeclIr union)
    {
        var name = context.Name(union);
        var typeParams = union.TypeParams.Length == 0 ? "" : "<" + string.Join(", ", union.TypeParams) + ">";
        var cases = Cases(context, union).ToArray();
        var caseUnion = cases.Length == 0 ? "never" : string.Join(" | ", cases.Select(c => CaseType(context, union, c.Decl)));

        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        foreach (var import in cases.Select(c => context.Name(c.Decl)).Where(n => n != name).Distinct().OrderBy(n => n, StringComparer.Ordinal))
            w.Line("import type { " + import + " } from './" + import + ".js';");
        w.Line();

        var doc = "C# " + JsSyntax.Code(JsContext.ClrName(union)) + ": a union over " + (union.IsInterface ? "an interface" : "an abstract class")
                  + ", written as " + JsSyntax.Code("[tag, value]") + ". Cases: " + CaseList(context, union) + "." + Generic(union);
        if (union.IsInterface)
        {
            JsSyntax.Doc(w, union.Docs, doc);
            w.Line("export type " + name + typeParams + " = " + caseUnion + ";");
            w.Line();
            JsSyntax.Doc(w, JsSyntax.Code("value instanceof " + name) + " is true for an instance of a case.");
            using (w.Block("export declare const " + name + ":", "{", "};"))
                w.Line("[Symbol.hasInstance](value: unknown): boolean;");
            return w.ToString();
        }

        JsSyntax.Doc(w, union.Docs, doc, "Abstract: create one of its cases.");
        using (w.Block("export declare abstract class " + name + typeParams))
        {
            w.Line("protected constructor(init?: unknown);");
            // Nominal: only the case classes (which extend this class) are assignable.
            w.Line("private readonly $nexnetUnion" + name + ";");
        }

        w.Line();
        using (w.Block("export declare namespace " + name))
        {
            JsSyntax.Doc(w, "The case types of " + JsSyntax.Code(name) + ".");
            w.Line("type Case" + typeParams + " = " + caseUnion + ";");
        }

        return w.ToString();
    }

    // ------------------------------------------------------------------ enums

    /// <summary>A member value as a JavaScript literal (bigint for 64-bit enums; the IR holds UInt64 values as raw bits).</summary>
    public static string EnumValue(EnumDeclIr e, long value) => e.Underlying switch
    {
        PrimKind.UInt64 => unchecked((ulong)value).ToString(System.Globalization.CultureInfo.InvariantCulture) + "n",
        PrimKind.Int64 => JsSyntax.Int(value) + "n",
        _ => JsSyntax.Int(value),
    };

    private static string EnumJs(JsContext context, EnumDeclIr e)
    {
        var name = context.Name(e);
        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line();
        JsSyntax.Doc(w, e.Docs, "C# " + JsSyntax.Code(JsContext.ClrName(e)) + ": " + (e.IsFlags ? "[Flags] " : "") + "enum of "
                                + JsSyntax.Code(e.Underlying.ToString()) + ", written as its value. C# enums are open: other values can arrive.");
        w.Line("export const " + name + " = Object.freeze({").Indent();
        foreach (var member in e.Members)
            w.Line(context.Target.Names.EnumMember(member.Name) + ": " + EnumValue(e, member.Value) + ",");
        w.Unindent().Line("});");

        return w.ToString();
    }

    private static string EnumDts(JsContext context, EnumDeclIr e)
    {
        var name = context.Name(e);
        var w = new CodeWriter("  ");
        w.Line(JsSyntax.Header);
        w.Line();
        JsSyntax.Doc(w, e.Docs, "C# " + JsSyntax.Code(JsContext.ClrName(e)) + ": " + (e.IsFlags ? "[Flags] " : "") + "enum of "
                                + JsSyntax.Code(e.Underlying.ToString()) + ", written as its value.");
        using (w.Block("export declare const " + name + ":", "{", "};"))
        {
            foreach (var member in e.Members)
                w.Line("readonly " + context.Target.Names.EnumMember(member.Name) + ": " + EnumValue(e, member.Value) + ";");
        }

        w.Line();
        var scalar = JsValues.Is64(e.Underlying) ? "bigint" : "number";
        var values = e.Members.Select(m => EnumValue(e, m.Value)).Distinct().ToArray();
        if (e.IsFlags || values.Length == 0)
        {
            JsSyntax.Doc(w, "A value of " + JsSyntax.Code(name) + ": flags combine, so any " + scalar + ".");
            w.Line("export type " + name + " = " + scalar + ";");
        }
        else
        {
            JsSyntax.Doc(w, "A value of " + JsSyntax.Code(name) + ".");
            w.Line("export type " + name + " = " + string.Join(" | ", values) + ";");
        }

        return w.ToString();
    }

    // ------------------------------------------------------------------ declaration imports

    /// <summary>The runtime types and generated types a declaration file names, written as type imports.</summary>
    internal sealed class DtsImports
    {
        private readonly JsContext _context;
        private readonly string _self;
        private readonly SortedSet<string> _runtime = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _decls = new(StringComparer.Ordinal);

        public DtsImports(JsContext context, string selfId)
        {
            _context = context;
            _self = selfId;
        }

        public void Add(TypeRef? type)
        {
            if (type == null)
                return;
            switch (type.Kind)
            {
                case TypeRefKind.Prim when RuntimeTypeName(type.Prim) is { } runtime:
                    _runtime.Add(runtime);
                    break;
                case TypeRefKind.Decl when _context.Scope.Find(type.DeclId) is { } decl && decl.Id != _self:
                    _decls.Add(_context.Name(decl));
                    break;
            }

            foreach (var arg in type.Args)
                Add(arg);
        }

        /// <summary>Writes the imports; the base class is a value import (a class it extends).</summary>
        public void Write(CodeWriter w, TypeDeclIr? baseClass)
        {
            if (_runtime.Count > 0)
                w.Line("import type { " + string.Join(", ", _runtime) + " } from " + JsSyntax.String(_context.RuntimeModule) + ";");
            var baseName = baseClass == null ? null : _context.Name(baseClass);
            if (baseName != null)
                w.Line("import { " + baseName + " } from './" + baseName + ".js';");
            foreach (var decl in _decls)
            {
                if (decl != baseName)
                    w.Line("import type { " + decl + " } from './" + decl + ".js';");
            }
        }

        /// <summary>The runtime type of a built-in leaf with no JavaScript primitive.</summary>
        public static string? RuntimeTypeName(PrimKind prim) => prim switch
        {
            PrimKind.Decimal => "NexusDecimal",
            PrimKind.DateTime => "NexusDateTime",
            PrimKind.DateTimeOffset => "NexusDateTimeOffset",
            PrimKind.TimeSpan => "NexusTimeSpan",
            PrimKind.DateOnly => "NexusDateOnly",
            PrimKind.TimeOnly => "NexusTimeOnly",
            PrimKind.Version => "Version",
            _ => null,
        };
    }
}
