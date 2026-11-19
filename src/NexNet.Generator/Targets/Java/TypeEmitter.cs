using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.Java;

/// <summary>
/// Emits one Java file per declared type into the flat package (<c>&lt;package&gt;/&lt;Outer_Inner&gt;.java</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>
/// <c>[NexusObject]</c> classes, structs and records become mutable POJOs: private fields, a no-argument constructor
/// (members at their C# initializer or <c>default(T)</c>), an all-members constructor in key order, <c>getX</c>/<c>setX</c>
/// accessors, and value <c>equals</c>/<c>hashCode</c>/<c>toString</c> for every object (so any object works in a
/// <c>Set</c>, as a <c>Map</c> key and with a synchronized list's <c>remove</c>). Structs are <c>final</c>. A
/// <c>[NexusObject]</c> base class is extended when it is not generic; otherwise its members are copied.
/// </item>
/// <item>Enums are open value classes: a constant per member, <c>value()</c>, <c>of(v)</c> for any value of the
/// underlying type, and <c>or</c>/<c>and</c>/<c>has</c> for <c>[Flags]</c> enums.</item>
/// <item>Unions over an abstract class are an abstract class; unions over an interface are an interface. Case
/// classes extend or implement them.</item>
/// <item>Nested .NET types are flattened to <c>Outer_Inner</c>.</item>
/// </list>
/// </remarks>
internal static class TypeEmitter
{
    /// <summary>The getter of a member: <c>get</c> + the mapped property name.</summary>
    public static string Getter(JavaContext context, MemberIr member) => "get" + JavaSyntax.UpperFirst(context.Target.Names.Property(member.Name));

    private static string Setter(JavaContext context, MemberIr member) => "set" + JavaSyntax.UpperFirst(context.Target.Names.Property(member.Name));

    private static string Field(JavaContext context, MemberIr member) => context.Target.Names.Property(member.Name);

    public static string Emit(JavaContext context, TypeDeclIr decl, IReadOnlyDictionary<string, List<UnionDeclIr>> unionsOfCase)
    {
        var w = new CodeWriter();
        w.Line(JavaSyntax.Header);
        w.Line("package " + context.Package + ";");
        w.Line();
        switch (decl)
        {
            case ObjectDeclIr obj:
                WriteObject(w, context, obj, unionsOfCase);
                break;
            case UnionDeclIr union:
                WriteUnion(w, context, union);
                break;
            case EnumDeclIr e:
                WriteEnum(w, context, e);
                break;
        }

        return w.ToString();
    }

    private static string Clr(TypeDeclIr decl) => "{@code " + JavaSyntax.DocText(decl.ClrMetadataName) + "}";

    private static string Generic(IEnumerable<string> typeParams)
    {
        var list = typeParams.ToArray();
        return list.Length == 0 ? "" : "<" + string.Join(", ", list) + ">";
    }

    // ------------------------------------------------------------------ objects

    /// <summary>The base class a Java object extends: a non-generic [NexusObject] base or abstract-class union.</summary>
    private static TypeDeclIr? Base(JavaContext context, ObjectDeclIr obj)
        => context.Scope.Find(obj.BaseDeclId) switch
        {
            ObjectDeclIr { TypeParams.Length: 0 } b => b,
            UnionDeclIr { IsInterface: false, TypeParams.Length: 0 } u => u,
            _ => null,
        };

    private static void WriteObject(CodeWriter w, JavaContext context, ObjectDeclIr obj, IReadOnlyDictionary<string, List<UnionDeclIr>> unionsOfCase)
    {
        var scope = context.Scope.WithTypeParams(obj.TypeParams.ToArray());
        var baseDecl = Base(context, obj);
        var baseObject = baseDecl as ObjectDeclIr;
        var baseMembers = new HashSet<string>(baseObject?.Members.Select(m => m.Name) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var all = obj.Members.ToArray();
        var own = all.Where(m => !baseMembers.Contains(m.Name)).ToArray();
        var name = context.Name(obj);
        var generic = Generic(obj.TypeParams);
        var interfaces = unionsOfCase.TryGetValue(obj.Id, out var unions)
            ? unions.Where(u => u.IsInterface && u.TypeParams.Length == 0).Select(context.Name).ToArray()
            : Array.Empty<string>();

        string Type(MemberIr m) => context.Simple.Map(m.Type, scope).Name;
        string DeclaredType(MemberIr m) => m.Type.MayBeNull ? JavaSyntax.Nullable(Type(m)) : Type(m);

        if (all.Any(m => m.Type.MayBeNull))
        {
            w.Line(JavaSyntax.NullableImport);
            w.Line();
        }

        var what = obj.IsStruct ? "struct" : obj.IsRecord ? "record" : "class";
        var formatter = obj.IsAbstract
            ? "It is abstract and has no formatter."
            : "Its formatter is {@code " + context.FormattersPackage + ".Formatters." + name + (obj.TypeParams.Length > 0 ? "(...)" : "") + "}.";
        JavaSyntax.Javadoc(w, JavaSyntax.Doc(obj.Docs, "The C# " + what + " " + Clr(obj) + "."), DocParagraph(
            "<p>C# " + what + " " + Clr(obj) + ", serialized as a MessagePack array of " + JavaSyntax.Int(obj.ArrayLength) + " elements. " + formatter,
            obj.TypeParams.Select(p => "@param <" + p + "> the C# type parameter " + p).ToArray()));

        var header = "public " + (obj.IsAbstract ? "abstract " : obj.IsStruct ? "final " : "") + "class " + name + generic;
        if (baseDecl != null)
            header += " extends " + context.Name(baseDecl);
        if (interfaces.Length > 0)
            header += " implements " + string.Join(", ", interfaces);

        using (w.Block(header))
        {
            // Fields
            foreach (var m in own)
            {
                var initial = JavaValues.Literal(m.DefaultLiteral, m.Type, scope, context.Name, context.Target.Names)
                              ?? JavaValues.Default(m.Type, scope, context.Name);
                w.Line("private " + DeclaredType(m) + " " + Field(context, m) + (initial == null ? "" : " = " + initial) + ";");
            }

            if (own.Length > 0)
                w.Line();

            var visibility = obj.IsAbstract ? "protected " : "public ";

            // Constructors
            JavaSyntax.Javadoc(w, "Creates an instance with every member at its C# initial value.");
            using (w.Block(visibility + name + "()"))
            {
            }

            if (all.Length > 0)
            {
                w.Line();
                var tags = all.Select(m => "@param " + Field(context, m) + " the value of " + MemberDoc(m)).ToArray();
                JavaSyntax.Javadoc(w, "Creates an instance with every member set.", tags);
                using (w.Block(visibility + name + "(" + string.Join(", ", all.Select(m => DeclaredType(m) + " " + Field(context, m))) + ")"))
                {
                    if (baseObject != null && baseObject.Members.Length > 0)
                        w.Line("super(" + string.Join(", ", baseObject.Members.Select(b => Field(context, all.First(m => m.Name == b.Name)))) + ");");
                    foreach (var m in own)
                        w.Line("this." + Field(context, m) + " = " + Field(context, m) + ";");
                }
            }

            // Accessors
            foreach (var m in own)
            {
                var field = Field(context, m);
                w.Line();
                JavaSyntax.Javadoc(w, JavaSyntax.Doc(m.Docs, "Gets " + MemberDoc(m) + "."), "@return the value");
                using (w.Block("public " + DeclaredType(m) + " " + Getter(context, m) + "()"))
                    w.Line("return this." + field + ";");

                w.Line();
                JavaSyntax.Javadoc(w, "Sets " + MemberDoc(m) + ".", "@param " + field + " the value");
                using (w.Block("public void " + Setter(context, m) + "(" + DeclaredType(m) + " " + field + ")"))
                    w.Line("this." + field + " = " + field + ";");
            }

            // equals / hashCode / toString
            var callsSuper = baseObject != null;
            w.Line();
            w.Line("@Override");
            using (w.Block("public boolean equals(Object obj)"))
            {
                using (w.Block("if (this == obj)"))
                    w.Line("return true;");
                using (w.Block("if (obj == null || getClass() != obj.getClass())"))
                    w.Line("return false;");
                if (callsSuper)
                {
                    using (w.Block("if (!super.equals(obj))"))
                        w.Line("return false;");
                }

                if (own.Length == 0)
                {
                    w.Line("return true;");
                }
                else
                {
                    var wildcard = Generic(obj.TypeParams.Select(_ => "?"));
                    w.Line(name + wildcard + " other = (" + name + wildcard + ") obj;");
                    var terms = own.Select(m => EqualsTerm(Type(m), "this." + Field(context, m), "other." + Field(context, m))).ToArray();
                    w.Line("return " + string.Join("\n    && ", terms).Replace("\n", "\n" + new string(' ', 4 * (w.IndentLevel))) + ";");
                }
            }

            w.Line();
            w.Line("@Override");
            using (w.Block("public int hashCode()"))
            {
                var values = (callsSuper ? new[] { "super.hashCode()" } : Array.Empty<string>())
                    .Concat(own.Select(m => HashTerm(Type(m), "this." + Field(context, m))))
                    .ToArray();
                w.Line(values.Length == 0
                    ? "return " + JavaSyntax.StringLiteral(name) + ".hashCode();"
                    : "return java.util.Arrays.deepHashCode(new Object[] {" + string.Join(", ", values) + "});");
            }

            w.Line();
            w.Line("@Override");
            using (w.Block("public String toString()"))
            {
                if (all.Length == 0)
                {
                    w.Line("return " + JavaSyntax.StringLiteral(name + "{}") + ";");
                }
                else
                {
                    var parts = all.Select((m, i) =>
                    {
                        var label = JavaSyntax.StringLiteral((i == 0 ? name + "{" : ", ") + Field(context, m) + "=");
                        var value = Type(m).EndsWith("[]", StringComparison.Ordinal)
                            ? "java.util.Arrays.toString(" + Getter(context, m) + "())"
                            : Getter(context, m) + "()";
                        return label + " + " + value;
                    });
                    w.Line("return " + string.Join(" + ", parts) + " + \"}\";");
                }
            }
        }
    }

    private static string[] DocParagraph(string text, string[] tags)
    {
        var lines = new List<string> { text };
        if (tags.Length > 0)
            lines.Add("");
        lines.AddRange(tags);
        return lines.ToArray();
    }

    private static string MemberDoc(MemberIr m) => "the C# member {@code " + JavaSyntax.DocText(m.Name) + "} (key " + JavaSyntax.Int(m.Key) + ")";

    /// <summary>
    /// Field equality: primitives by value (floating point by bits, as the boxed types), deques (which have no value
    /// equality) by their elements, the rest deep.
    /// </summary>
    private static string EqualsTerm(string javaType, string left, string right) => javaType switch
    {
        "float" => "Float.compare(" + left + ", " + right + ") == 0",
        "double" => "Double.compare(" + left + ", " + right + ") == 0",
        _ when JavaSyntax.IsPrimitive(javaType) => left + " == " + right,
        _ when IsDeque(javaType) => "(" + left + " == null ? " + right + " == null : " + right + " != null && java.util.Arrays.deepEquals("
                                    + left + ".toArray(), " + right + ".toArray()))",
        _ => "java.util.Objects.deepEquals(" + left + ", " + right + ")",
    };

    private static bool IsDeque(string javaType) => javaType.StartsWith("java.util.ArrayDeque<", StringComparison.Ordinal);

    /// <summary>The value a field contributes to the hash code (deques by their elements, as in equals).</summary>
    private static string HashTerm(string javaType, string field)
        => IsDeque(javaType) ? "(" + field + " == null ? null : " + field + ".toArray())" : field;

    // ------------------------------------------------------------------ unions

    private static void WriteUnion(CodeWriter w, JavaContext context, UnionDeclIr union)
    {
        var name = context.Name(union);
        var cases = string.Join(", ", union.Cases
            .Select(c => (c.Tag, Decl: context.Scope.Find(c.CaseDeclId)))
            .Where(c => c.Decl != null)
            .Select(c => "{@link " + context.Name(c.Decl!) + "} (tag " + JavaSyntax.Int(c.Tag) + ")"));
        var kind = union.IsInterface ? "interface" : "abstract class";
        JavaSyntax.Javadoc(w, JavaSyntax.Doc(union.Docs, "The C# union " + Clr(union) + "."), DocParagraph(
            "<p>C# union " + Clr(union) + ", serialized as {@code [tag, value]}. Cases: " + (cases.Length == 0 ? "none" : cases) + ".",
            union.TypeParams.Select(p => "@param <" + p + "> the C# type parameter " + p).ToArray()));

        using (w.Block("public " + kind + " " + name + Generic(union.TypeParams)))
        {
            if (!union.IsInterface)
            {
                JavaSyntax.Javadoc(w, "Creates an instance of a case.");
                using (w.Block("protected " + name + "()"))
                {
                }
            }
        }
    }

    // ------------------------------------------------------------------ enums

    private static void WriteEnum(CodeWriter w, JavaContext context, EnumDeclIr e)
    {
        var name = context.Name(e);
        var type = JavaValues.EnumValueType(e.Underlying);
        var unsigned64 = e.Underlying == PrimKind.UInt64;
        string Literal(long v) => JavaValues.EnumLiteral(v, e.Underlying);
        string Cast(string expression) => type is "byte" or "short" ? "(" + type + ") (" + expression + ")" : expression;

        // The first member of each value names it (C# aliases share a value).
        var named = new List<EnumMemberIr>();
        var values = new HashSet<long>();
        foreach (var m in e.Members)
        {
            if (values.Add(m.Value))
                named.Add(m);
        }

        JavaSyntax.Javadoc(w, JavaSyntax.Doc(e.Docs, "The C# enum " + Clr(e) + "."),
            "<p>C# " + (e.IsFlags ? "[Flags] " : "") + "enum " + Clr(e) + " (underlying {@code " + e.Underlying + "}), serialized as its",
            "value. An open value class: {@link #of} accepts every value of the underlying type, named or not.");
        using (w.Block("public final class " + name))
        {
            foreach (var m in e.Members)
            {
                JavaSyntax.Javadoc(w, "C# {@code " + JavaSyntax.DocText(m.Name) + "} = " + (unsigned64 ? ((ulong)m.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : JavaSyntax.Int(m.Value)) + ".");
                w.Line("public static final " + name + " " + context.Target.Names.EnumMember(m.Name) + " = new " + name + "(" + Literal(m.Value) + ");");
                w.Line();
            }

            w.Line("private final " + type + " value$;");
            w.Line();
            using (w.Block("private " + name + "(" + type + " value)"))
                w.Line("this.value$ = value;");

            w.Line();
            JavaSyntax.Javadoc(w, "The underlying value" + (unsigned64 ? " (the raw bits of the C# {@code ulong})" : type == "long" && e.Underlying == PrimKind.UInt32 ? " (0 to 4294967295)" : "") + ".", "@return the value");
            using (w.Block("public " + type + " value()"))
                w.Line("return this.value$;");

            w.Line();
            JavaSyntax.Javadoc(w, "The value with this underlying value: the member constant when one has it, otherwise a new instance.",
                "@param value the underlying value", "@return the value");
            using (w.Block("public static " + name + " of(" + type + " value)"))
            {
                foreach (var m in named)
                {
                    using (w.Block("if (value == " + Literal(m.Value) + ")"))
                        w.Line("return " + context.Target.Names.EnumMember(m.Name) + ";");
                }

                w.Line("return new " + name + "(value);");
            }

            if (e.IsFlags)
            {
                w.Line();
                JavaSyntax.Javadoc(w, "The bitwise OR of two values.", "@param other the other value", "@return the combined value");
                using (w.Block("public " + name + " or(" + name + " other)"))
                    w.Line("return of(" + Cast("this.value$ | other.value$") + ");");

                w.Line();
                JavaSyntax.Javadoc(w, "The bitwise AND of two values.", "@param other the other value", "@return the common flags");
                using (w.Block("public " + name + " and(" + name + " other)"))
                    w.Line("return of(" + Cast("this.value$ & other.value$") + ");");

                w.Line();
                JavaSyntax.Javadoc(w, "True when every flag of {@code flags} is set, as C# {@code HasFlag}.", "@param flags the flags", "@return whether all are set");
                using (w.Block("public boolean has(" + name + " flags)"))
                    w.Line("return (this.value$ & flags.value$) == flags.value$;");
            }

            w.Line();
            w.Line("@Override");
            using (w.Block("public boolean equals(Object obj)"))
                w.Line("return obj instanceof " + name + " && ((" + name + ") obj).value$ == this.value$;");

            w.Line();
            w.Line("@Override");
            using (w.Block("public int hashCode()"))
                w.Line("return Long.hashCode(this.value$);");

            var number = unsigned64 ? JavaTarget.RuntimePackage + ".UnsignedLongs.toString(this.value$)" : "String.valueOf(this.value$)";
            w.Line();
            JavaSyntax.Javadoc(w, "The C# name of the value" + (e.IsFlags ? " (flag names joined by \", \" for a combination)" : "") + ", or its number when no member names it.",
                "@return the text");
            w.Line("@Override");
            using (w.Block("public String toString()"))
            {
                foreach (var m in named)
                {
                    using (w.Block("if (this.value$ == " + Literal(m.Value) + ")"))
                        w.Line("return " + JavaSyntax.StringLiteral(m.Name) + ";");
                }

                if (e.IsFlags)
                    WriteFlagNames(w, e, named, type);

                w.Line("return " + number + ";");
            }
        }
    }

    /// <summary>A combination as C# writes it: the named flags, largest first taken, listed in ascending order.</summary>
    private static void WriteFlagNames(CodeWriter w, EnumDeclIr e, List<EnumMemberIr> named, string type)
    {
        long Mask(long v) => type switch
        {
            "byte" => v & 0xFF,
            "short" => v & 0xFFFF,
            "int" when e.Underlying is PrimKind.Int32 => v & 0xFFFFFFFFL,
            _ => v,
        };

        var flags = named
            .Where(m => m.Value != 0)
            .OrderByDescending(m => (ulong)Mask(m.Value))
            .ToArray();
        if (flags.Length == 0)
            return;

        var bits = type switch
        {
            "byte" => "this.value$ & 0xFFL",
            "short" => "this.value$ & 0xFFFFL",
            "int" => "this.value$ & 0xFFFFFFFFL",
            _ => "this.value$",
        };
        w.Line("long rest = " + bits + ";");
        w.Line("StringBuilder names = new StringBuilder();");
        foreach (var m in flags)
        {
            var literal = JavaSyntax.Int(Mask(m.Value)) + "L";
            using (w.Block("if ((rest & " + literal + ") == " + literal + ")"))
            {
                w.Line("names.insert(0, names.length() == 0 ? " + JavaSyntax.StringLiteral(m.Name) + " : " + JavaSyntax.StringLiteral(m.Name + ", ") + ");");
                w.Line("rest &= ~" + literal + ";");
            }
        }

        using (w.Block("if (rest == 0L && names.length() > 0)"))
            w.Line("return names.toString();");
    }
}
