using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.Java;

/// <summary>
/// Emits <c>&lt;package&gt;/formatters/Formatters.java</c>: a <c>public static final Formatter&lt;X&gt;</c> constant per
/// generated type (named as the type, <c>Formatters.InteropItem</c>) and a factory per generic type
/// (<c>Formatters.InteropBox(Formatter&lt;T&gt;)</c>), ports of the C# <c>EmitObject</c> and <c>EmitUnion</c>; and
/// <c>UserFormatters.java</c>, which instantiates the hand-written formatters of <c>NexusTargetFormatter</c> mappings (D12).
/// </summary>
/// <remarks>
/// <para>
/// Static initialization: the type constants are created first and hold no other formatter; formatters composed from
/// them (<c>list(Formatters.InteropItem)</c>) are private constants declared after every type constant and read only
/// when a value is written or read. A generic formatter composes its member formatters in its constructor from its
/// arguments; a reference to itself is <c>this</c> and a reference to another recursive generic type is created lazily.
/// </para>
/// <para>
/// Objects are written as an array of <c>ArrayLength</c> elements with nil in the key gaps. Reading accepts fewer
/// elements (an older peer: the missing members are C# <c>default(T)</c>) and more (a newer peer: skipped). Struct
/// types refuse null both ways. Unions are <c>[tag, value]</c>; the writer tests deeper cases first.
/// </para>
/// </remarks>
internal static class FormatterEmitter
{
    /// <summary>The sub-package of the formatter classes.</summary>
    public const string SubPackage = "formatters";

    public const string ClassName = "Formatters";

    public const string UserClassName = "UserFormatters";

    private const string Rt = JavaSyntax.RuntimeFormatters + ".";

    /// <summary>Type names that would collide with the formatter classes or their package (NEXNET041).</summary>
    internal static readonly string[] ReservedTypeNames = { ClassName, UserClassName, SubPackage };

    /// <summary>The nested formatter class of a declared type.</summary>
    private static string ImplName(string typeName) => "Fmt$" + typeName;

    /// <summary>The expression builder for code in the formatter package (the classes written by simple name).</summary>
    public static FormatterExpressions Expressions(JavaContext context) => new(context.Scope, ClassName, UserClassName);

    public static IEnumerable<GeneratedFile> Emit(JavaContext context, IReadOnlyList<TypeDeclIr> decls, IReadOnlyList<UserFormatterRef> userFormatters, string directory)
    {
        yield return new GeneratedFile(directory + ClassName + ".java", Formatters(context, decls));
        if (userFormatters.Count > 0)
            yield return new GeneratedFile(directory + UserClassName + ".java", UserFormattersFile(context, userFormatters));
    }

    // ------------------------------------------------------------------ Formatters.java

    private static string Formatters(JavaContext context, IReadOnlyList<TypeDeclIr> decls)
    {
        var expressions = Expressions(context);
        var composites = new CompositeTable();
        var nested = new CodeWriter();
        var usesSeq = false;
        var usesLazy = false;

        foreach (var decl in decls)
        {
            if (decl is ObjectDeclIr { IsAbstract: true })
                continue;

            var typeParamCount = TypeParams(decl).Length;
            var local = typeParamCount > 0 ? expressions.ForGeneric(decl.Id, typeParamCount) : expressions;
            nested.Line();
            switch (decl)
            {
                case ObjectDeclIr obj:
                    WriteObject(nested, context, obj, local, composites);
                    break;
                case UnionDeclIr union:
                    WriteUnion(nested, context, union, local, composites);
                    break;
                case EnumDeclIr e:
                    WriteEnum(nested, context, e);
                    break;
            }

            usesSeq |= local.UsesSeq;
            usesLazy |= local.UsesLazy;
        }

        usesSeq |= expressions.UsesSeq;
        usesLazy |= expressions.UsesLazy;

        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + context.FormattersPackage + ";");
        w.Line();
        w.Line("import com.dtronix.nexnet.serialization.Formatter;");
        w.Line("import com.dtronix.nexnet.serialization.MsgPackException;");
        w.Line("import com.dtronix.nexnet.serialization.MsgPackReader;");
        w.Line("import com.dtronix.nexnet.serialization.MsgPackWriter;");
        w.Line();
        JavaSyntax.Javadoc(w, null,
            "MessagePack formatters of the types generated in {@code " + context.Package + "}: a constant per type and a",
            "factory per generic type, taking the formatters of its type arguments. The formatters write the bytes of the",
            "C# NexNet serializer. They are stateless and thread-safe. Types of C# structs and enums refuse null.");
        using (w.Block("public final class " + ClassName))
        {
            var first = true;
            foreach (var decl in decls)
            {
                if (decl is ObjectDeclIr { IsAbstract: true })
                    continue;
                if (!first)
                    w.Line();
                first = false;
                WriteEntryPoint(w, context, decl);
            }

            foreach (var (expression, type, field) in composites.Entries)
            {
                w.Line();
                w.Line("private static final Formatter<" + type + "> " + field + " =");
                w.Indent().Line(expression + ";").Unindent();
            }

            w.Line();
            using (w.Block("private " + ClassName + "()"))
            {
            }

            w.Lines(nested.ToString().TrimEnd('\n'));

            if (usesLazy)
            {
                w.Line();
                w.Lines(LazyHelper);
            }

            if (usesSeq)
            {
                w.Line();
                w.Lines(SeqHelper);
            }
        }

        return w.ToString();
    }

    private static string[] TypeParams(TypeDeclIr decl) => decl switch
    {
        ObjectDeclIr o => o.TypeParams.ToArray(),
        UnionDeclIr u => u.TypeParams.ToArray(),
        _ => Array.Empty<string>(),
    };

    /// <summary>The public constant or factory of a type.</summary>
    private static void WriteEntryPoint(CodeWriter w, JavaContext context, TypeDeclIr decl)
    {
        var name = context.Name(decl);
        var typeParams = TypeParams(decl);
        var clr = "{@code " + JavaSyntax.DocText(decl.ClrMetadataName) + "}";
        if (typeParams.Length == 0)
        {
            JavaSyntax.Javadoc(w, "The formatter of {@link " + context.QualifiedName(decl) + "} (C# " + clr + ").");
            w.Line("public static final Formatter<" + context.QualifiedName(decl) + "> " + name + " = new " + ImplName(name) + "();");
            return;
        }

        var tags = new List<string>();
        for (var i = 0; i < typeParams.Length; i++)
            tags.Add("@param " + FormatterExpressions.TypeParamFormatter(i) + " the formatter of {@code " + typeParams[i] + "}");
        foreach (var p in typeParams)
            tags.Add("@param <" + p + "> the type argument");
        tags.Add("@return the formatter");
        JavaSyntax.Javadoc(w, "Creates the formatter of {@link " + context.QualifiedName(decl) + "} (C# " + clr + ") for the given type arguments.", tags.ToArray());

        var generic = "<" + string.Join(", ", typeParams) + ">";
        var parameters = string.Join(", ", typeParams.Select((p, i) => "Formatter<" + p + "> " + FormatterExpressions.TypeParamFormatter(i)));
        using (w.Block("public static " + generic + " Formatter<" + context.QualifiedName(decl) + generic + "> " + name + "(" + parameters + ")"))
        {
            for (var i = 0; i < typeParams.Length; i++)
            {
                var f = FormatterExpressions.TypeParamFormatter(i);
                w.Line("java.util.Objects.requireNonNull(" + f + ", " + JavaSyntax.StringLiteral(f) + ");");
            }

            w.Line("return new " + ImplName(name) + "<>(" + string.Join(", ", typeParams.Select((_, i) => FormatterExpressions.TypeParamFormatter(i))) + ");");
        }
    }

    /// <summary>
    /// The formatters a nested class uses: simple references inline, composed ones hoisted into a static constant
    /// (non-generic) or an instance field set in the constructor (generic).
    /// </summary>
    private sealed class MemberFormatters
    {
        private readonly bool _generic;
        private readonly CompositeTable _composites;
        private readonly ITypeMapper _types;
        private readonly TypeScope _scope;
        private readonly FormatterExpressions _expressions;
        private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);

        public MemberFormatters(bool generic, CompositeTable composites, ITypeMapper types, TypeScope scope, FormatterExpressions expressions)
        {
            _generic = generic;
            _composites = composites;
            _types = types;
            _scope = scope;
            _expressions = expressions;
        }

        /// <summary>Instance fields of a generic formatter: (Java type, name, initializer).</summary>
        public List<(string Type, string Name, string Expression)> Fields { get; } = new();

        public string For(TypeRef type)
        {
            var expression = _expressions.For(type);
            if (expression == "this" || expression.IndexOf('(') < 0)
                return expression;

            var javaType = JavaTarget.Box(_types.Map(type, _scope).Name);
            if (!_generic)
                return _composites.Get(expression, javaType);

            if (!_fields.TryGetValue(expression, out var field))
            {
                field = "c" + JavaSyntax.Int(Fields.Count);
                Fields.Add((javaType, field, expression));
                _fields[expression] = field;
            }

            return field;
        }
    }

    /// <summary>Writes a nested formatter class: type parameter formatters and composed fields, then the body.</summary>
    private static void WriteImpl(CodeWriter w, JavaContext context, TypeDeclIr decl, MemberFormatters formatters, Action<CodeWriter> body)
    {
        var name = context.Name(decl);
        var typeParams = TypeParams(decl);
        var generic = typeParams.Length == 0 ? "" : "<" + string.Join(", ", typeParams) + ">";
        var javaType = context.QualifiedName(decl) + generic;

        // The body is written first so the fields it needs are known.
        var bodyWriter = new CodeWriter();
        body(bodyWriter);

        using (w.Block("private static final class " + ImplName(name) + generic + " implements Formatter<" + javaType + ">"))
        {
            if (typeParams.Length > 0)
            {
                for (var i = 0; i < typeParams.Length; i++)
                    w.Line("private final Formatter<" + typeParams[i] + "> " + FormatterExpressions.TypeParamFormatter(i) + ";");
                foreach (var field in formatters.Fields)
                    w.Line("private final Formatter<" + field.Type + "> " + field.Name + ";");
                w.Line();
                var parameters = string.Join(", ", typeParams.Select((p, i) => "Formatter<" + p + "> " + FormatterExpressions.TypeParamFormatter(i)));
                using (w.Block(ImplName(name) + "(" + parameters + ")"))
                {
                    for (var i = 0; i < typeParams.Length; i++)
                    {
                        var f = FormatterExpressions.TypeParamFormatter(i);
                        w.Line("this." + f + " = " + f + ";");
                    }

                    foreach (var field in formatters.Fields)
                        w.Line("this." + field.Name + " = " + field.Expression + ";");
                }

                w.Line();
            }

            w.Lines(bodyWriter.ToString().TrimEnd('\n'));
        }
    }

    private static void WriteObject(CodeWriter w, JavaContext context, ObjectDeclIr obj, FormatterExpressions expressions, CompositeTable composites)
    {
        var scope = context.Scope.WithTypeParams(obj.TypeParams.ToArray());
        var formatters = new MemberFormatters(obj.TypeParams.Length > 0, composites, context.Qualified, scope, expressions);
        var name = context.Name(obj);
        var generic = obj.TypeParams.Length == 0 ? "" : "<" + string.Join(", ", obj.TypeParams) + ">";
        var javaType = context.QualifiedName(obj) + generic;
        var members = obj.Members.ToArray();

        WriteImpl(w, context, obj, formatters, b =>
        {
            b.Line("@Override");
            using (b.Block("public void write(MsgPackWriter writer, " + javaType + " value)"))
            {
                using (b.Block("if (value == null)"))
                {
                    if (obj.IsStruct)
                    {
                        b.Line("throw new MsgPackException(MsgPackException.Kind.TYPE, " + JavaSyntax.StringLiteral(name + " is a value type and cannot be null.") + ");");
                    }
                    else
                    {
                        b.Line("writer.writeNil();");
                        b.Line("return;");
                    }
                }

                b.Line("writer.writeArrayHeader(" + JavaSyntax.Int(obj.ArrayLength) + ");");
                var byKey = new Dictionary<int, MemberIr>();
                foreach (var m in members)
                {
                    if (!byKey.ContainsKey(m.Key))
                        byKey[m.Key] = m;
                }

                for (var key = 0; key < obj.ArrayLength; key++)
                {
                    if (byKey.TryGetValue(key, out var m))
                        b.Line(formatters.For(m.Type) + ".write(writer, value." + TypeEmitter.Getter(context, m) + "());");
                    else
                        b.Line("writer.writeNil();");
                }
            }

            b.Line();
            b.Line("@Override");
            using (b.Block("public " + javaType + " read(MsgPackReader reader)"))
            {
                if (obj.IsStruct)
                {
                    using (b.Block("if (reader.isNil())"))
                        b.Line("throw new MsgPackException(MsgPackException.Kind.TYPE, " + JavaSyntax.StringLiteral("nil is not a value of the value type " + name + ".") + ");");
                }
                else
                {
                    using (b.Block("if (reader.tryReadNil())"))
                        b.Line("return null;");
                }

                b.Line("int count = reader.readArrayHeader();");
                b.Line("reader.enter();");
                for (var i = 0; i < members.Length; i++)
                {
                    var type = context.Qualified.Map(members[i].Type, scope).Name;
                    var initial = JavaValues.Default(members[i].Type, scope, context.QualifiedName) ?? JavaSyntax.Zero(type);
                    b.Line(type + " m" + JavaSyntax.Int(i) + " = " + initial + ";");
                }

                using (b.Block("for (int i = 0; i < count; i++)"))
                {
                    if (members.Length == 0)
                    {
                        b.Line("reader.skip();");
                    }
                    else
                    {
                        using (b.Block("switch (i)"))
                        {
                            var seen = new HashSet<int>();
                            for (var i = 0; i < members.Length; i++)
                            {
                                if (!seen.Add(members[i].Key))
                                    continue;
                                b.Line("case " + JavaSyntax.Int(members[i].Key) + ":");
                                b.Indent();
                                b.Line("m" + JavaSyntax.Int(i) + " = " + formatters.For(members[i].Type) + ".read(reader);");
                                b.Line("break;");
                                b.Unindent();
                            }

                            b.Line("default:");
                            b.Indent().Line("reader.skip();").Line("break;").Unindent();
                        }
                    }
                }

                b.Line("reader.exit();");
                var diamond = obj.TypeParams.Length == 0 ? "" : "<>";
                b.Line("return new " + context.QualifiedName(obj) + diamond + "(" + string.Join(", ", members.Select((_, i) => "m" + JavaSyntax.Int(i))) + ");");
            }
        });
    }

    private static void WriteUnion(CodeWriter w, JavaContext context, UnionDeclIr union, FormatterExpressions expressions, CompositeTable composites)
    {
        var scope = context.Scope.WithTypeParams(union.TypeParams.ToArray());
        var formatters = new MemberFormatters(union.TypeParams.Length > 0, composites, context.Qualified, scope, expressions);
        var name = context.Name(union);
        var generic = union.TypeParams.Length == 0 ? "" : "<" + string.Join(", ", union.TypeParams) + ">";
        var javaType = context.QualifiedName(union) + generic;
        var cases = union.Cases
            .Select(c => (Case: c, Decl: context.Scope.Find(c.CaseDeclId)))
            .Where(c => c.Decl != null)
            .ToArray();

        WriteImpl(w, context, union, formatters, b =>
        {
            b.Line("@Override");
            using (b.Block("public void write(MsgPackWriter writer, " + javaType + " value)"))
            {
                using (b.Block("if (value == null)"))
                {
                    b.Line("writer.writeNil();");
                    b.Line("return;");
                }

                // Deeper cases first, so a subclass case is not shadowed by its base case (stable for ties).
                foreach (var (unionCase, decl) in cases.OrderByDescending(c => c.Case.InheritanceDepth))
                {
                    var caseType = context.QualifiedName(decl!);
                    var caseRef = new TypeRef(TypeRefKind.Decl) { DeclId = decl!.Id };
                    using (b.Block("if (value instanceof " + caseType + ")"))
                    {
                        b.Line("writer.writeArrayHeader(2);");
                        b.Line("writer.writeInt(" + JavaSyntax.Int(unionCase.Tag) + ");");
                        b.Line(formatters.For(caseRef) + ".write(writer, (" + caseType + ") value);");
                        b.Line("return;");
                    }
                }

                b.Line("throw new MsgPackException(MsgPackException.Kind.TYPE, value.getClass().getName() + "
                       + JavaSyntax.StringLiteral(" is not a case of the union " + name + ".") + ");");
            }

            b.Line();
            b.Line("@Override");
            using (b.Block("public " + javaType + " read(MsgPackReader reader)"))
            {
                using (b.Block("if (reader.tryReadNil())"))
                    b.Line("return null;");
                b.Line("reader.readArrayHeader(2);");
                b.Line("int tag = reader.readUInt16();");
                b.Line("reader.enter();");
                b.Line(javaType + " result;");
                using (b.Block("switch (tag)"))
                {
                    foreach (var (unionCase, decl) in cases)
                    {
                        var caseRef = new TypeRef(TypeRefKind.Decl) { DeclId = decl!.Id };
                        b.Line("case " + JavaSyntax.Int(unionCase.Tag) + ":");
                        b.Indent();
                        b.Line("result = " + formatters.For(caseRef) + ".read(reader);");
                        b.Line("break;");
                        b.Unindent();
                    }

                    b.Line("default:");
                    b.Indent();
                    b.Line("throw new MsgPackException(MsgPackException.Kind.VALUE, \"Unknown tag \" + tag + "
                           + JavaSyntax.StringLiteral(" of the union " + name + ".") + ");");
                    b.Unindent();
                }

                b.Line("reader.exit();");
                b.Line("return result;");
            }
        });
    }

    private static void WriteEnum(CodeWriter w, JavaContext context, EnumDeclIr e)
    {
        var name = context.Name(e);
        var javaType = context.QualifiedName(e);
        var underlying = Rt + FormatterExpressions.PrimFormatter(e.Underlying);
        using (w.Block("private static final class " + ImplName(name) + " implements Formatter<" + javaType + ">"))
        {
            w.Line("@Override");
            using (w.Block("public void write(MsgPackWriter writer, " + javaType + " value)"))
            {
                using (w.Block("if (value == null)"))
                    w.Line("throw new MsgPackException(MsgPackException.Kind.TYPE, " + JavaSyntax.StringLiteral(name + " is a value type and cannot be null.") + ");");
                w.Line(underlying + ".write(writer, value.value());");
            }

            w.Line();
            w.Line("@Override");
            using (w.Block("public " + javaType + " read(MsgPackReader reader)"))
                w.Line("return " + javaType + ".of(" + underlying + ".read(reader));");
        }
    }

    // ------------------------------------------------------------------ UserFormatters.java

    /// <summary>A native formatter class and the .NET types it serializes.</summary>
    internal sealed record UserFormatterRef(string Formatter, IReadOnlyList<string> ClrTypes);

    private static string UserFormattersFile(JavaContext context, IReadOnlyList<UserFormatterRef> formatters)
    {
        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + context.FormattersPackage + ";");
        w.Line();
        w.Line("import com.dtronix.nexnet.serialization.Formatter;");
        w.Line();
        JavaSyntax.Javadoc(w, null,
            "The hand-written formatters of the .NET types serialized by a custom {@code NexusFormatter}, declared with",
            "{@code [assembly: NexusTargetFormatter<T>(NexusTargets.Java, \"<class>\")]}. Each class below must be on the",
            "compile classpath, implement {@link Formatter} for the Java type it chooses, have a public no-argument",
            "constructor and write the same bytes as the C# formatter. Generated members of these types are typed",
            "{@code Object}. A missing class fails the compilation.",
            "",
            "<ul>",
            string.Join("\n * ", formatters.Select(f => "<li>{@code " + JavaSyntax.DocText(f.Formatter) + "}: "
                + string.Join(", ", f.ClrTypes.Select(t => "{@code " + JavaSyntax.DocText(t) + "}")) + "</li>")),
            "</ul>");
        using (w.Block("public final class " + UserClassName))
        {
            foreach (var f in formatters)
            {
                JavaSyntax.Javadoc(w, "The formatter of " + string.Join(", ", f.ClrTypes.Select(t => "{@code " + JavaSyntax.DocText(t) + "}")) + ".");
                w.Line("public static final Formatter<Object> " + FormatterExpressions.UserFormatterField(f.Formatter) + " = cast(new " + f.Formatter + "());");
                w.Line();
            }

            using (w.Block("private " + UserClassName + "()"))
            {
            }

            w.Line();
            w.Line("@SuppressWarnings(\"unchecked\")");
            using (w.Block("private static Formatter<Object> cast(Formatter<?> formatter)"))
                w.Line("return (Formatter<Object>) formatter;");
        }

        return w.ToString();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Hoisted composed formatters of the non-generic classes, deduplicated by expression.</summary>
    private sealed class CompositeTable
    {
        private readonly Dictionary<string, string> _byExpression = new(StringComparer.Ordinal);

        public List<(string Expression, string Type, string Field)> Entries { get; } = new();

        public string Get(string expression, string javaType)
        {
            if (!_byExpression.TryGetValue(expression, out var field))
            {
                field = "C$" + JavaSyntax.Int(Entries.Count);
                Entries.Add((expression, javaType, field));
                _byExpression[expression] = field;
            }

            return ClassName + "." + field;
        }
    }

    private const string LazyHelper = """
        /** A formatter created on first use: breaks the construction cycle of mutually recursive generic types. */
        static <T> Formatter<T> lazy(java.util.function.Supplier<Formatter<T>> factory) {
            return new Lazy<>(factory);
        }

        private static final class Lazy<T> implements Formatter<T> {
            private final java.util.function.Supplier<Formatter<T>> factory;
            private volatile Formatter<T> formatter;

            Lazy(java.util.function.Supplier<Formatter<T>> factory) {
                this.factory = factory;
            }

            private Formatter<T> get() {
                Formatter<T> f = formatter;
                if (f == null) {
                    f = factory.get();
                    formatter = f;
                }
                return f;
            }

            @Override
            public void write(MsgPackWriter writer, T value) {
                get().write(writer, value);
            }

            @Override
            public T read(MsgPackReader reader) {
                return get().read(reader);
            }
        }
        """;

    // C# resolves the formatter of T[] and List<T> when T is bound: ext 78 for the primitive element kinds, bin for
    // byte[] (List<byte> stays an array). The Java member is a List<T>; this converts it to the primitive array.
    private const string SeqHelper = """
        /**
         * The formatter of a C# {@code T[]} ({@code array}) or {@code List<T>} of a type parameter: as C# does once
         * {@code T} is bound, an ext 78 primitive array for the primitive element formatters, bin for {@code byte[]},
         * otherwise an array of elements.
         */
        static <T> Formatter<java.util.List<T>> seq(Formatter<T> element, boolean array) {
            int kind = PackedSeq.kindOf(element, array);
            return kind < 0 ? com.dtronix.nexnet.serialization.Formatters.list(element) : new PackedSeq<T>(kind);
        }

        private static final class PackedSeq<T> implements Formatter<java.util.List<T>> {
            private static final Formatter<?>[] ELEMENTS = {
                com.dtronix.nexnet.serialization.Formatters.BYTE,
                com.dtronix.nexnet.serialization.Formatters.SBYTE,
                com.dtronix.nexnet.serialization.Formatters.SHORT,
                com.dtronix.nexnet.serialization.Formatters.USHORT,
                com.dtronix.nexnet.serialization.Formatters.INT,
                com.dtronix.nexnet.serialization.Formatters.UINT,
                com.dtronix.nexnet.serialization.Formatters.LONG,
                com.dtronix.nexnet.serialization.Formatters.ULONG,
                com.dtronix.nexnet.serialization.Formatters.FLOAT,
                com.dtronix.nexnet.serialization.Formatters.DOUBLE,
                com.dtronix.nexnet.serialization.Formatters.HALF,
                com.dtronix.nexnet.serialization.Formatters.CHAR,
            };

            private final int kind;

            PackedSeq(int kind) {
                this.kind = kind;
            }

            static int kindOf(Formatter<?> element, boolean array) {
                for (int i = array ? 0 : 1; i < ELEMENTS.length; i++) {
                    if (ELEMENTS[i] == element) {
                        return i;
                    }
                }
                return -1;
            }

            private static Object item(java.util.List<?> list, int index) {
                Object item = list.get(index);
                if (item == null) {
                    throw new MsgPackException(MsgPackException.Kind.TYPE, "A primitive array cannot hold null.");
                }
                return item;
            }

            private static MsgPackException range(Object value, String type) {
                return new MsgPackException(MsgPackException.Kind.RANGE, value + " is outside the C# " + type + " range.");
            }

            @Override
            public void write(MsgPackWriter writer, java.util.List<T> value) {
                if (value == null) {
                    writer.writeNil();
                    return;
                }
                int n = value.size();
                switch (kind) {
                    case 0: {
                        byte[] a = new byte[n];
                        for (int i = 0; i < n; i++) {
                            int v = (Integer) item(value, i);
                            if (v < 0 || v > 255) {
                                throw range(v, "byte");
                            }
                            a[i] = (byte) v;
                        }
                        com.dtronix.nexnet.serialization.Formatters.BINARY.write(writer, a);
                        return;
                    }
                    case 1: {
                        byte[] a = new byte[n];
                        for (int i = 0; i < n; i++) {
                            a[i] = (Byte) item(value, i);
                        }
                        com.dtronix.nexnet.serialization.Formatters.SBYTE_ARRAY.write(writer, a);
                        return;
                    }
                    case 2: {
                        short[] a = new short[n];
                        for (int i = 0; i < n; i++) {
                            a[i] = (Short) item(value, i);
                        }
                        com.dtronix.nexnet.serialization.Formatters.SHORT_ARRAY.write(writer, a);
                        return;
                    }
                    case 3: {
                        char[] a = new char[n];
                        for (int i = 0; i < n; i++) {
                            int v = (Integer) item(value, i);
                            if (v < 0 || v > 65535) {
                                throw range(v, "ushort");
                            }
                            a[i] = (char) v;
                        }
                        com.dtronix.nexnet.serialization.Formatters.USHORT_ARRAY.write(writer, a);
                        return;
                    }
                    case 4: {
                        int[] a = new int[n];
                        for (int i = 0; i < n; i++) {
                            a[i] = (Integer) item(value, i);
                        }
                        com.dtronix.nexnet.serialization.Formatters.INT_ARRAY.write(writer, a);
                        return;
                    }
                    case 5: {
                        int[] a = new int[n];
                        for (int i = 0; i < n; i++) {
                            long v = (Long) item(value, i);
                            if (v < 0 || v > 0xFFFFFFFFL) {
                                throw range(v, "uint");
                            }
                            a[i] = (int) v;
                        }
                        com.dtronix.nexnet.serialization.Formatters.UINT_ARRAY.write(writer, a);
                        return;
                    }
                    case 6:
                    case 7: {
                        long[] a = new long[n];
                        for (int i = 0; i < n; i++) {
                            a[i] = (Long) item(value, i);
                        }
                        (kind == 6 ? com.dtronix.nexnet.serialization.Formatters.LONG_ARRAY
                            : com.dtronix.nexnet.serialization.Formatters.ULONG_ARRAY).write(writer, a);
                        return;
                    }
                    case 8:
                    case 10: {
                        float[] a = new float[n];
                        for (int i = 0; i < n; i++) {
                            a[i] = (Float) item(value, i);
                        }
                        (kind == 8 ? com.dtronix.nexnet.serialization.Formatters.FLOAT_ARRAY
                            : com.dtronix.nexnet.serialization.Formatters.HALF_ARRAY).write(writer, a);
                        return;
                    }
                    case 9: {
                        double[] a = new double[n];
                        for (int i = 0; i < n; i++) {
                            a[i] = (Double) item(value, i);
                        }
                        com.dtronix.nexnet.serialization.Formatters.DOUBLE_ARRAY.write(writer, a);
                        return;
                    }
                    default: {
                        char[] a = new char[n];
                        for (int i = 0; i < n; i++) {
                            a[i] = (Character) item(value, i);
                        }
                        com.dtronix.nexnet.serialization.Formatters.CHAR_ARRAY.write(writer, a);
                        return;
                    }
                }
            }

            @Override
            @SuppressWarnings("unchecked")
            public java.util.List<T> read(MsgPackReader reader) {
                if (reader.tryReadNil()) {
                    return null;
                }
                java.util.List<Object> result;
                switch (kind) {
                    case 0: {
                        byte[] a = com.dtronix.nexnet.serialization.Formatters.BINARY.read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (byte v : a) {
                            result.add(v & 0xFF);
                        }
                        break;
                    }
                    case 1: {
                        byte[] a = com.dtronix.nexnet.serialization.Formatters.SBYTE_ARRAY.read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (byte v : a) {
                            result.add(v);
                        }
                        break;
                    }
                    case 2: {
                        short[] a = com.dtronix.nexnet.serialization.Formatters.SHORT_ARRAY.read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (short v : a) {
                            result.add(v);
                        }
                        break;
                    }
                    case 3: {
                        char[] a = com.dtronix.nexnet.serialization.Formatters.USHORT_ARRAY.read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (char v : a) {
                            result.add((int) v);
                        }
                        break;
                    }
                    case 4: {
                        int[] a = com.dtronix.nexnet.serialization.Formatters.INT_ARRAY.read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (int v : a) {
                            result.add(v);
                        }
                        break;
                    }
                    case 5: {
                        int[] a = com.dtronix.nexnet.serialization.Formatters.UINT_ARRAY.read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (int v : a) {
                            result.add(v & 0xFFFFFFFFL);
                        }
                        break;
                    }
                    case 6:
                    case 7: {
                        long[] a = (kind == 6 ? com.dtronix.nexnet.serialization.Formatters.LONG_ARRAY
                            : com.dtronix.nexnet.serialization.Formatters.ULONG_ARRAY).read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (long v : a) {
                            result.add(v);
                        }
                        break;
                    }
                    case 8:
                    case 10: {
                        float[] a = (kind == 8 ? com.dtronix.nexnet.serialization.Formatters.FLOAT_ARRAY
                            : com.dtronix.nexnet.serialization.Formatters.HALF_ARRAY).read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (float v : a) {
                            result.add(v);
                        }
                        break;
                    }
                    case 9: {
                        double[] a = com.dtronix.nexnet.serialization.Formatters.DOUBLE_ARRAY.read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (double v : a) {
                            result.add(v);
                        }
                        break;
                    }
                    default: {
                        char[] a = com.dtronix.nexnet.serialization.Formatters.CHAR_ARRAY.read(reader);
                        result = new java.util.ArrayList<>(a.length);
                        for (char v : a) {
                            result.add(v);
                        }
                        break;
                    }
                }
                return (java.util.List<T>) (java.util.List<?>) result;
            }
        }
        """;
}
