using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using NexNet.Generator.Models;

namespace NexNet.Generator.Serialization;

/// <summary>
/// A serialization diagnostic produced during extraction and reported during output (MessagePack backend only).
/// </summary>
internal sealed record SerializationDiagnostic(string Id, string Arg0, string Arg1, LocationData? Location);

/// <summary>
/// Walks the closure of types reachable from serialization roots and generates file-scoped formatter classes
/// plus a module initializer that registers every formatter needed at runtime.
/// Runs in the transform phase (symbol access) and produces plain strings for the output phase.
/// </summary>
internal sealed class SerializationBuilder
{
    private const string Ns = "global::NexNet.Serialization";
    private const string FormattersNs = "global::NexNet.Serialization.Formatters";

    private static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                              | SymbolDisplayMiscellaneousOptions.ExpandNullable);

    private static readonly HashSet<string> BuiltInLeafTypes = new(StringComparer.Ordinal)
    {
        "System.DateTimeOffset", "System.TimeSpan", "System.DateOnly", "System.TimeOnly", "System.Guid", "System.Half",
        "System.Numerics.BigInteger", "System.Uri", "System.Version",
    };

    private readonly Compilation _compilation;
    private readonly string _uniqueSuffix;
    private readonly Dictionary<ITypeSymbol, string> _userFormatters = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ITypeSymbol> _visited = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ITypeSymbol, string> _formatterClassNames = new(SymbolEqualityComparer.Default);
    private readonly StringBuilder _classes = new();
    private readonly List<string> _registrations = new();
    private readonly HashSet<string> _registrationSet = new(StringComparer.Ordinal);
    private readonly List<SerializationDiagnostic> _diagnostics = new();

    public SerializationBuilder(Compilation compilation, string uniqueSuffix)
    {
        _compilation = compilation;
        _uniqueSuffix = uniqueSuffix;
        CollectAssemblyAttributes();
    }

    public IReadOnlyList<SerializationDiagnostic> Diagnostics => _diagnostics;

    public bool HasOutput => _registrations.Count > 0;

    /// <summary>
    /// Fully qualified type name used in generated serialization code.
    /// </summary>
    public static string TypeName(ITypeSymbol type)
    {
        return type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(TypeFormat);
    }

    private void CollectAssemblyAttributes()
    {
        foreach (var attr in _compilation.Assembly.GetAttributes())
        {
            var cls = attr.AttributeClass;
            if (cls is not { IsGenericType: true })
                continue;

            if (cls.Name == "NexusFormatterAttribute" && cls.TypeArguments.Length == 2
                && cls.ContainingNamespace?.ToDisplayString() == "NexNet.Serialization")
            {
                _userFormatters[cls.TypeArguments[1]] = TypeName(cls.TypeArguments[0]);
            }
        }
    }

    /// <summary>
    /// Adds the assembly-level [NexusSerializable&lt;T&gt;] declarations as roots.
    /// </summary>
    public void AddAssemblyDeclaredRoots()
    {
        foreach (var attr in _compilation.Assembly.GetAttributes())
        {
            var cls = attr.AttributeClass;
            if (cls is { IsGenericType: true, Name: "NexusSerializableAttribute" } && cls.TypeArguments.Length == 1
                && cls.ContainingNamespace?.ToDisplayString() == "NexNet.Serialization")
            {
                Require(cls.TypeArguments[0], "[assembly: NexusSerializable]", null);
            }
        }

        foreach (var pair in _userFormatters)
            Require(pair.Key, "[assembly: NexusFormatter]", null);
    }

    /// <summary>
    /// Ensures a formatter for <paramref name="type"/> (and everything it contains) is registered.
    /// </summary>
    public void Require(ITypeSymbol type, string context, LocationData? location)
    {
        if (type is ITypeParameterSymbol)
            return; // Open generic parameters are resolved at runtime through the registry.

        var normalized = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (!_visited.Add(normalized))
            return;

        var name = TypeName(normalized);

        if (_userFormatters.TryGetValue(normalized, out var userFormatter))
        {
            AddRegistration(name, $"new {userFormatter}()");
            return;
        }

        switch (normalized.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
            case SpecialType.System_DateTime:
                return; // Built in.
        }

        if (normalized.TypeKind == TypeKind.Enum)
        {
            AddRegistration(name, $"{FormattersNs}.EnumFormatter<{name}>.Instance");
            return;
        }

        if (normalized is IArrayTypeSymbol array)
        {
            if (array.Rank != 1)
            {
                Report("NEXNET028", name, context, location);
                return;
            }

            var element = array.ElementType;
            var elementName = TypeName(element);
            if (element.SpecialType == SpecialType.System_Byte)
                return; // byte[] is bin.

            if (IsPrimitiveKind(element))
            {
                AddRegistration(name, $"{Ns}.PrimitiveArrayFormatter<{elementName}>.Instance");
                return;
            }

            AddRegistration(name, $"new {FormattersNs}.ArrayFormatter<{elementName}>()");
            Require(element, context, location);
            return;
        }

        if (normalized is not INamedTypeSymbol named)
        {
            Report("NEXNET028", name, context, location);
            return;
        }

        if (BuiltInLeafTypes.Contains(MetadataFullName(named)))
            return;

        if (named.IsGenericType && TryRequireGenericBuiltIn(named, name, context, location))
            return;

        if (HasAttribute(named, "NexusObjectAttribute"))
        {
            RequireNexusObject(named, name, context, location);
            return;
        }

        Report("NEXNET028", name, context, location);
    }

    private bool TryRequireGenericBuiltIn(INamedTypeSymbol named, string name, string context, LocationData? location)
    {
        var def = MetadataFullName(named.OriginalDefinition);
        var args = named.TypeArguments;
        string Arg(int i) => TypeName(args[i]);

        void RequireArgs()
        {
            foreach (var a in args)
                Require(a, context, location);
        }

        switch (def)
        {
            case "System.Nullable`1":
                AddRegistration(name, $"new {FormattersNs}.NullableFormatter<{Arg(0)}>()");
                RequireArgs();
                return true;

            case "System.Collections.Generic.List`1":
                if (args[0].SpecialType == SpecialType.System_Byte)
                {
                    AddRegistration(name, $"new {FormattersNs}.ListFormatter<{Arg(0)}>()");
                    return true;
                }

                AddRegistration(name, IsPrimitiveKind(args[0])
                    ? $"{Ns}.PrimitiveListFormatter<{Arg(0)}>.Instance"
                    : $"new {FormattersNs}.ListFormatter<{Arg(0)}>()");
                RequireArgs();
                return true;

            case "System.Collections.Generic.IList`1":
            case "System.Collections.Generic.IReadOnlyList`1":
            case "System.Collections.Generic.ICollection`1":
            case "System.Collections.Generic.IReadOnlyCollection`1":
            case "System.Collections.Generic.IEnumerable`1":
                AddRegistration(name, $"new {FormattersNs}.InterfaceListFormatter<{name}, {Arg(0)}>()");
                RequireArgs();
                return true;

            case "System.Collections.Generic.HashSet`1":
                AddRegistration(name, $"new {FormattersNs}.HashSetFormatter<{Arg(0)}>()");
                RequireArgs();
                return true;

            case "System.Collections.Generic.Queue`1":
                AddRegistration(name, $"new {FormattersNs}.QueueFormatter<{Arg(0)}>()");
                RequireArgs();
                return true;

            case "System.Collections.Generic.Stack`1":
                AddRegistration(name, $"new {FormattersNs}.StackFormatter<{Arg(0)}>()");
                RequireArgs();
                return true;

            case "System.Collections.Generic.Dictionary`2":
                AddRegistration(name, $"new {FormattersNs}.DictionaryFormatter<{Arg(0)}, {Arg(1)}>()");
                RequireArgs();
                return true;

            case "System.Collections.Generic.IDictionary`2":
            case "System.Collections.Generic.IReadOnlyDictionary`2":
                AddRegistration(name, $"new {FormattersNs}.InterfaceDictionaryFormatter<{name}, {Arg(0)}, {Arg(1)}>()");
                RequireArgs();
                return true;

            case "System.Collections.Generic.KeyValuePair`2":
                AddRegistration(name, $"new {FormattersNs}.KeyValuePairFormatter<{Arg(0)}, {Arg(1)}>()");
                RequireArgs();
                return true;

            case "System.ValueTuple`1":
            case "System.ValueTuple`2":
            case "System.ValueTuple`3":
            case "System.ValueTuple`4":
            case "System.ValueTuple`5":
            case "System.ValueTuple`6":
            case "System.ValueTuple`7":
                AddRegistration(name, $"new {FormattersNs}.ValueTupleFormatter<{string.Join(", ", args.Select(TypeName))}>()");
                RequireArgs();
                return true;

            case "System.Tuple`1":
            case "System.Tuple`2":
            case "System.Tuple`3":
            case "System.Tuple`4":
            case "System.Tuple`5":
            case "System.Tuple`6":
            case "System.Tuple`7":
                AddRegistration(name, $"new {FormattersNs}.TupleFormatter<{string.Join(", ", args.Select(TypeName))}>()");
                RequireArgs();
                return true;

            case "System.Memory`1":
            case "System.ReadOnlyMemory`1":
            case "System.ArraySegment`1":
            case "System.Buffers.ReadOnlySequence`1":
                if (args[0].SpecialType == SpecialType.System_Byte)
                    return true; // bin, built in.

                if ((def == "System.Memory`1" || def == "System.ReadOnlyMemory`1") && IsPrimitiveKind(args[0]))
                {
                    var formatter = def == "System.Memory`1" ? "PrimitiveMemoryFormatter" : "PrimitiveReadOnlyMemoryFormatter";
                    AddRegistration(name, $"{Ns}.{formatter}<{Arg(0)}>.Instance");
                    return true;
                }

                Report("NEXNET028", name, context, location);
                return true;
        }

        return false;
    }

    private static bool IsPrimitiveKind(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Char:
                return true;
        }

        return MetadataFullName(type) == "System.Half";
    }

    private void AddRegistration(string typeName, string expression)
    {
        var statement = $"{Ns}.NexusFormatterRegistry.Register<{typeName}>({expression});";
        if (_registrationSet.Add(statement))
            _registrations.Add(statement);
    }

    private void Report(string id, string arg0, string arg1, LocationData? location)
    {
        var diag = new SerializationDiagnostic(id, arg0, arg1, location);
        if (!_diagnostics.Contains(diag))
            _diagnostics.Add(diag);
    }

    internal static string MetadataFullName(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        var name = type.MetadataName;
        var containing = type.ContainingType;
        while (containing != null)
        {
            name = containing.MetadataName + "+" + name;
            containing = containing.ContainingType;
        }

        return ns is null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
    }

    internal static bool HasAttribute(ISymbol symbol, string attributeName)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name == attributeName
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "NexNet.Serialization")
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ [NexusObject] types

    private void RequireNexusObject(INamedTypeSymbol type, string name, string context, LocationData? location)
    {
        var definition = type.OriginalDefinition;
        var location2 = LocationData.FromSymbol(definition) ?? location;

        if (!IsAccessible(definition))
        {
            Report("NEXNET036", TypeName(definition), "", location2);
            return;
        }

        if (!_formatterClassNames.TryGetValue(definition, out var className))
        {
            className = "__NexusFormatter_" + Sanitize(definition.Name) + "_" + _formatterClassNames.Count + "_" + _uniqueSuffix;
            _formatterClassNames[definition] = className;
            EmitFormatterClass(definition, className);
        }

        var typeArgs = type.IsGenericType ? "<" + string.Join(", ", type.TypeArguments.Select(TypeName)) + ">" : "";
        AddRegistration(name, $"new {className}{typeArgs}()");

        // Walk the closure of member and union case types using the constructed type (substituted members).
        if (TryGetUnionCases(type, out var cases))
        {
            foreach (var (_, caseType) in cases)
                Require(caseType, TypeName(type), location2);
        }
        else
        {
            foreach (var member in GetKeyedMembers(type, out _))
                Require(member.Type, TypeName(type) + "." + member.Name, location2);
        }
    }

    private bool IsAccessible(ISymbol symbol) => _compilation.IsSymbolAccessibleWithin(symbol, _compilation.Assembly);

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private sealed class KeyedMember
    {
        public ISymbol Symbol = null!;
        public string Name = null!;
        public ITypeSymbol Type = null!;
        public int Key;
        public bool IsField;
        public bool CanGet;
        public bool CanSet;
        public bool IsInitOnly;
        public bool IsRequired;
        public IFieldSymbol? BackingField;
        public string LocalName = null!;
    }

    private List<KeyedMember> GetKeyedMembers(INamedTypeSymbol type, out List<SerializationDiagnostic> problems)
    {
        problems = new List<SerializationDiagnostic>();
        var result = new List<KeyedMember>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        for (INamedTypeSymbol? current = type; current != null && current.SpecialType != SpecialType.System_Object
             && current.SpecialType != SpecialType.System_ValueType; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic)
                    continue;

                ITypeSymbol? memberType;
                bool isField;
                if (member is IPropertySymbol prop && !prop.IsIndexer)
                {
                    memberType = prop.Type;
                    isField = false;
                }
                else if (member is IFieldSymbol { AssociatedSymbol: null } field)
                {
                    memberType = field.Type;
                    isField = true;
                }
                else
                {
                    continue;
                }

                if (!seenNames.Add(member.Name))
                    continue; // Overridden/hidden members are handled once.

                var keyAttr = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "NexusKeyAttribute"
                    && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "NexNet.Serialization");
                var ignored = HasAttribute(member, "NexusIgnoreAttribute");

                if (keyAttr == null)
                {
                    if (!ignored && !member.IsImplicitlyDeclared && member.DeclaredAccessibility == Accessibility.Public && IsSerializableShape(member))
                        problems.Add(new SerializationDiagnostic("NEXNET030", TypeName(type), member.Name, LocationData.FromSymbol(member)));
                    continue;
                }

                if (ignored)
                    continue;

                var km = new KeyedMember
                {
                    Symbol = member,
                    Name = member.Name,
                    Type = memberType,
                    Key = keyAttr.ConstructorArguments.Length > 0 && keyAttr.ConstructorArguments[0].Value is int k ? k : 0,
                    IsField = isField,
                };

                if (member is IPropertySymbol p)
                {
                    km.CanGet = p.GetMethod != null && IsAccessible(p.GetMethod);
                    km.IsInitOnly = p.SetMethod?.IsInitOnly == true;
                    km.CanSet = p.SetMethod != null && IsAccessible(p.SetMethod);
                    km.IsRequired = p.IsRequired;
                    km.BackingField = p.ContainingType.GetMembers().OfType<IFieldSymbol>()
                        .FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, p));
                }
                else
                {
                    var f = (IFieldSymbol)member;
                    var accessible = IsAccessible(f);
                    km.CanGet = accessible;
                    km.CanSet = accessible && !f.IsReadOnly && !f.IsConst;
                    km.IsRequired = f.IsRequired;
                }

                result.Add(km);
            }
        }

        result.Sort((a, b) => a.Key.CompareTo(b.Key));
        for (var i = 0; i < result.Count; i++)
            result[i].LocalName = "__m" + i;

        return result;
    }

    private static bool IsSerializableShape(ISymbol member)
    {
        // Computed get-only properties are not data and do not need a key.
        if (member is IPropertySymbol p)
        {
            if (p.SetMethod != null)
                return true;
            return p.ContainingType.GetMembers().OfType<IFieldSymbol>()
                .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, p));
        }

        return member is IFieldSymbol { IsConst: false };
    }

    private bool TryGetUnionCases(INamedTypeSymbol type, out List<(ushort Tag, ITypeSymbol Type)> cases)
    {
        cases = new List<(ushort, ITypeSymbol)>();
        foreach (var attr in type.OriginalDefinition.GetAttributes())
        {
            var cls = attr.AttributeClass;
            if (cls is { IsGenericType: true, Name: "NexusUnionAttribute" } && cls.TypeArguments.Length == 1
                && cls.ContainingNamespace?.ToDisplayString() == "NexNet.Serialization"
                && attr.ConstructorArguments.Length > 0)
            {
                var tagValue = attr.ConstructorArguments[0].Value;
                var tag = tagValue is ushort us ? us : Convert.ToUInt16(tagValue);
                cases.Add((tag, cls.TypeArguments[0]));
            }
        }

        return cases.Count > 0;
    }

    private void EmitFormatterClass(INamedTypeSymbol definition, string className)
    {
        var typeName = TypeName(definition);
        var typeParams = definition.IsGenericType
            ? "<" + string.Join(", ", definition.TypeParameters.Select(t => t.Name)) + ">"
            : "";
        var location = LocationData.FromSymbol(definition);

        var sb = _classes;
        sb.Append("file sealed class ").Append(className).Append(typeParams)
            .Append(" : ").Append(Ns).Append(".NexusFormatter<").Append(typeName).AppendLine(">");
        sb.AppendLine("{");

        if (TryGetUnionCases(definition, out var cases))
        {
            EmitUnion(sb, definition, typeName, cases, location);
        }
        else if (definition.IsAbstract || definition.TypeKind == TypeKind.Interface)
        {
            Report("NEXNET034", typeName, "", location);
            EmitThrowingBody(sb, typeName);
        }
        else
        {
            EmitObject(sb, definition, typeName, location);
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitThrowingBody(StringBuilder sb, string typeName)
    {
        sb.Append("    public override void Serialize(ref ").Append(Ns).Append(".MsgPackWriter writer, ").Append(typeName)
            .AppendLine(" value) => throw new global::System.NotSupportedException();");
        sb.Append("    public override void Deserialize(ref ").Append(Ns).Append(".MsgPackReader reader, ref ").Append(typeName)
            .AppendLine(" value) => throw new global::System.NotSupportedException();");
    }

    private void EmitUnion(StringBuilder sb, INamedTypeSymbol definition, string typeName,
        List<(ushort Tag, ITypeSymbol Type)> cases, LocationData? location)
    {
        var seenTags = new HashSet<ushort>();
        foreach (var (tag, caseType) in cases)
        {
            if (!seenTags.Add(tag))
                Report("NEXNET033", typeName, tag.ToString(), location);

            var conversion = _compilation.ClassifyCommonConversion(caseType, definition);
            if (!conversion.Exists || !conversion.IsImplicit)
                Report("NEXNET033", typeName, TypeName(caseType), location);
        }

        // More-derived types first so subclass cases are not shadowed by base cases.
        var ordered = cases.OrderByDescending(c => InheritanceDepth(c.Type)).ToList();

        sb.Append("    public override void Serialize(ref ").Append(Ns).Append(".MsgPackWriter writer, ").Append(typeName).AppendLine(" value)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (value)");
        sb.AppendLine("        {");
        sb.AppendLine("            case null:");
        sb.AppendLine("                writer.WriteNil();");
        sb.AppendLine("                return;");
        var i = 0;
        foreach (var (tag, caseType) in ordered)
        {
            var caseName = TypeName(caseType);
            sb.Append("            case ").Append(caseName).Append(" __c").Append(i).AppendLine(":");
            sb.AppendLine("                writer.WriteArrayHeader(2);");
            sb.Append("                writer.Write((ushort)").Append(tag).AppendLine(");");
            sb.Append("                ").AppendLine(PrimitiveCodec.WriteStatement(caseName, "__c" + i, "writer"));
            sb.AppendLine("                return;");
            i++;
        }

        sb.AppendLine("            default:");
        sb.Append("                throw ").Append(Ns).Append(".NexusSerializationException.UnknownUnionType(value.GetType(), typeof(")
            .Append(typeName).AppendLine("));");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.Append("    public override void Deserialize(ref ").Append(Ns).Append(".MsgPackReader reader, ref ").Append(typeName).AppendLine(" value)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (reader.TryReadNil())");
        sb.AppendLine("        {");
        sb.AppendLine("            value = default;");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine("        reader.ReadArrayHeader(2);");
        sb.AppendLine("        var tag = reader.ReadUInt16();");
        sb.AppendLine("        reader.Enter();");
        sb.AppendLine("        switch (tag)");
        sb.AppendLine("        {");
        foreach (var (tag, caseType) in cases)
        {
            var caseName = TypeName(caseType);
            sb.Append("            case ").Append(tag).AppendLine(":");
            sb.AppendLine("            {");
            sb.Append("                ").Append(caseName).AppendLine(" __v = default;");
            sb.Append("                ").AppendLine(PrimitiveCodec.ReadStatement(caseName, "__v", "reader"));
            sb.AppendLine("                value = __v;");
            sb.AppendLine("                break;");
            sb.AppendLine("            }");
        }

        sb.AppendLine("            default:");
        sb.Append("                throw ").Append(Ns).Append(".NexusSerializationException.UnknownUnionTag(tag, typeof(")
            .Append(typeName).AppendLine("));");
        sb.AppendLine("        }");
        sb.AppendLine("        reader.Exit();");
        sb.AppendLine("    }");
    }

    private static int InheritanceDepth(ITypeSymbol type)
    {
        var depth = 0;
        for (var t = type.BaseType; t != null; t = t.BaseType)
            depth++;
        return depth;
    }

    private void EmitObject(StringBuilder sb, INamedTypeSymbol definition, string typeName, LocationData? location)
    {
        var members = GetKeyedMembers(definition, out var problems);
        foreach (var p in problems)
            Report(p.Id, p.Arg0, p.Arg1, p.Location ?? location);

        // Duplicate keys and large gaps.
        var keys = new HashSet<int>();
        foreach (var m in members)
        {
            if (m.Key < 0 || !keys.Add(m.Key))
                Report("NEXNET029", typeName, m.Name, LocationData.FromSymbol(m.Symbol) ?? location);
        }

        var arrayLength = members.Count == 0 ? 0 : members[members.Count - 1].Key + 1;
        if (arrayLength - members.Count > 16)
            Report("NEXNET037", typeName, (arrayLength - members.Count).ToString(), location);

        var isStruct = definition.IsValueType;
        var isGeneric = definition.IsGenericType;

        // Construction strategy.
        var constructors = definition.InstanceConstructors
            .Where(c => !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, definition)))
            .ToList();
        var explicitCtors = constructors.Where(c => HasAttribute(c, "NexusConstructorAttribute")).ToList();
        IMethodSymbol? ctor = null;
        if (explicitCtors.Count > 1)
        {
            Report("NEXNET032", typeName, "", location);
        }
        else if (explicitCtors.Count == 1)
        {
            ctor = explicitCtors[0];
        }
        else
        {
            var parameterless = constructors.FirstOrDefault(c => c.Parameters.Length == 0 && IsAccessible(c));
            if (parameterless == null && !isStruct)
            {
                var accessible = constructors.Where(c => c.Parameters.Length > 0 && IsAccessible(c)).ToList();
                if (accessible.Count == 1)
                    ctor = accessible[0];
                else
                    Report("NEXNET032", typeName, "", location);
            }
        }

        if (ctor != null && ctor.Parameters.Length == 0)
            ctor = null;

        // Map constructor parameters to members.
        var ctorArgs = new List<KeyedMember?>();
        var ctorMembers = new HashSet<KeyedMember>();
        if (ctor != null)
        {
            if (!IsAccessible(ctor))
                Report("NEXNET032", typeName, "", location);

            foreach (var param in ctor.Parameters)
            {
                var match = members.FirstOrDefault(m => string.Equals(m.Name, param.Name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    Report("NEXNET031", typeName, param.Name, location);
                    ctorArgs.Add(null);
                }
                else
                {
                    ctorArgs.Add(match);
                    ctorMembers.Add(match);
                }
            }
        }

        var needsAccessors = members.Any(m => !m.CanGet || (!ctorMembers.Contains(m) && !m.CanSet && !m.IsInitOnly));
        if (needsAccessors && isGeneric)
            Report("NEXNET031", typeName, "(non-public member on generic type)", location);

        foreach (var m in members)
        {
            if (ctorMembers.Contains(m) || m.CanSet || m.IsInitOnly)
                continue;

            // Needs an UnsafeAccessor: fields, private setters or get-only auto properties (backing field).
            if (m.IsField || (m.Symbol is IPropertySymbol p && (p.SetMethod != null || m.BackingField != null)))
                continue;

            Report("NEXNET031", typeName, m.Name, LocationData.FromSymbol(m.Symbol) ?? location);
        }

        var useInitializer = members.Any(m => !ctorMembers.Contains(m) && (m.IsInitOnly || m.IsRequired));
        var populate = ctor == null && !useInitializer;

        // UnsafeAccessors for inaccessible members (non-generic types only).
        var ownerParam = (isStruct ? "ref " : "") + typeName + " o";
        foreach (var m in members)
        {
            if (isGeneric)
                break;

            var declaring = TypeName(m.Symbol.ContainingType);
            var declaringParam = (isStruct ? "ref " : "") + declaring + " o";
            var mt = TypeName(m.Type);
            if (!m.CanGet)
            {
                if (m.IsField)
                    EmitFieldAccessor(sb, m.Name, m.Name, mt, declaringParam);
                else
                    EmitMethodAccessor(sb, "get_" + m.Name, "__get_" + m.Name, mt, declaringParam, null);
            }

            if (ctorMembers.Contains(m) || m.CanSet || m.IsInitOnly)
                continue;

            if (m.IsField)
            {
                if (m.CanGet)
                    EmitFieldAccessor(sb, m.Name, m.Name, mt, declaringParam);
            }
            else if (((IPropertySymbol)m.Symbol).SetMethod != null)
            {
                EmitMethodAccessor(sb, "set_" + m.Name, "__set_" + m.Name, "void", declaringParam, mt);
            }
            else if (m.BackingField != null)
            {
                EmitFieldAccessor(sb, m.BackingField.Name, "bf_" + m.Name, mt, declaringParam);
            }
        }

        _ = ownerParam;

        // Serialize
        sb.Append("    public override void Serialize(ref ").Append(Ns).Append(".MsgPackWriter writer, ").Append(typeName).AppendLine(" value)");
        sb.AppendLine("    {");
        if (!isStruct)
        {
            sb.AppendLine("        if (value is null)");
            sb.AppendLine("        {");
            sb.AppendLine("            writer.WriteNil();");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }

        sb.Append("        writer.WriteArrayHeader(").Append(arrayLength).AppendLine(");");
        var next = 0;
        foreach (var m in members)
        {
            if (m.Key < next)
                continue; // duplicate key, already reported

            while (next < m.Key)
            {
                sb.AppendLine("        writer.WriteNil();");
                next++;
            }

            var getter = m.CanGet
                ? "value." + Escape(m.Name)
                : m.IsField
                    ? "__acc_" + m.Name + "(" + (isStruct ? "ref " : "") + "value)"
                    : "__get_" + m.Name + "(" + (isStruct ? "ref " : "") + "value)";
            sb.Append("        ").AppendLine(PrimitiveCodec.WriteStatement(TypeName(m.Type), getter, "writer"));
            next++;
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        // Deserialize
        sb.Append("    public override void Deserialize(ref ").Append(Ns).Append(".MsgPackReader reader, ref ").Append(typeName).AppendLine(" value)");
        sb.AppendLine("    {");
        if (!isStruct)
        {
            sb.AppendLine("        if (reader.TryReadNil())");
            sb.AppendLine("        {");
            sb.AppendLine("            value = default;");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }

        sb.AppendLine("        var count = reader.ReadArrayHeader();");
        sb.AppendLine("        reader.Enter();");
        foreach (var m in members)
            sb.Append("        ").Append(TypeName(m.Type)).Append(' ').Append(m.LocalName).AppendLine(" = default;");

        // Fast path: the peer wrote exactly the members this version knows, so read them in key order without
        // the per-member dispatch. Gaps in the keys are nil on the wire and are skipped.
        var expectedCount = members.Count == 0 ? 0 : members[members.Count - 1].Key + 1;
        var membersByKey = new Dictionary<int, KeyedMember>();
        foreach (var m in members)
        {
            if (!membersByKey.ContainsKey(m.Key))
                membersByKey.Add(m.Key, m);
        }

        if (expectedCount > 0)
        {
            sb.Append("        if (count == ").Append(expectedCount).AppendLine(")");
            sb.AppendLine("        {");
            for (var key = 0; key < expectedCount; key++)
            {
                sb.Append("            ");
                if (membersByKey.TryGetValue(key, out var m))
                    sb.AppendLine(PrimitiveCodec.ReadStatement(TypeName(m.Type), m.LocalName, "reader"));
                else
                    sb.AppendLine("reader.Skip();");
            }

            sb.AppendLine("        }");
            sb.AppendLine("        else");
        }

        // General path: fewer members (older peer) or extra members (newer peer, skipped).
        sb.AppendLine("        for (var i = 0; i < count; i++)");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (i)");
        sb.AppendLine("            {");
        foreach (var m in membersByKey.Values.OrderBy(m => m.Key))
        {
            sb.Append("                case ").Append(m.Key).Append(": ")
                .Append(PrimitiveCodec.ReadStatement(TypeName(m.Type), m.LocalName, "reader")).AppendLine(" break;");
        }

        sb.AppendLine("                default: reader.Skip(); break;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");

        // Construct / populate
        if (populate)
        {
            if (!isStruct)
            {
                sb.Append("        if (value is null) value = new ").Append(typeName).AppendLine("();");
            }
        }
        else
        {
            sb.Append("        value = new ").Append(typeName).Append('(');
            if (ctor != null)
                sb.Append(string.Join(", ", ctorArgs.Select(a => a == null ? "default" : a.LocalName)));
            sb.Append(')');

            var initMembers = members.Where(m => !ctorMembers.Contains(m) && (m.IsInitOnly || (m.IsRequired && m.CanSet))).ToList();
            if (initMembers.Count > 0)
            {
                sb.Append(" { ");
                sb.Append(string.Join(", ", initMembers.Select(m => Escape(m.Name) + " = " + m.LocalName)));
                sb.Append(" }");
            }

            sb.AppendLine(";");
        }

        foreach (var m in members)
        {
            if (ctorMembers.Contains(m))
                continue;
            if (!populate && (m.IsInitOnly || (m.IsRequired && m.CanSet)))
                continue;

            var refPrefix = isStruct ? "ref " : "";
            if (m.CanSet)
                sb.Append("        value.").Append(Escape(m.Name)).Append(" = ").Append(m.LocalName).AppendLine(";");
            else if (m.IsInitOnly)
                continue;
            else if (m.IsField)
                sb.Append("        __acc_").Append(m.Name).Append('(').Append(refPrefix).Append("value) = ").Append(m.LocalName).AppendLine(";");
            else if (((IPropertySymbol)m.Symbol).SetMethod != null)
                sb.Append("        __set_").Append(m.Name).Append('(').Append(refPrefix).Append("value, ").Append(m.LocalName).AppendLine(");");
            else if (m.BackingField != null)
                sb.Append("        __acc_bf_").Append(m.Name).Append('(').Append(refPrefix).Append("value) = ").Append(m.LocalName).AppendLine(";");
        }

        sb.AppendLine("        reader.Exit();");
        sb.AppendLine("    }");
    }

    private static string Escape(string name) => SyntaxFactsHelper.IsKeyword(name) ? "@" + name : name;

    private static void EmitFieldAccessor(StringBuilder sb, string fieldName, string accessorSuffix, string fieldType, string ownerParam)
    {
        sb.Append("    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = \"")
            .Append(fieldName).AppendLine("\")]");
        sb.Append("    private static extern ref ").Append(fieldType).Append(" __acc_").Append(accessorSuffix).Append('(').Append(ownerParam).AppendLine(");");
    }

    private static void EmitMethodAccessor(StringBuilder sb, string methodName, string accessorName, string returnType, string ownerParam, string? valueType)
    {
        sb.Append("    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"")
            .Append(methodName).AppendLine("\")]");
        sb.Append("    private static extern ").Append(returnType).Append(' ').Append(accessorName).Append('(').Append(ownerParam);
        if (valueType != null)
            sb.Append(", ").Append(valueType).Append(" v");
        sb.AppendLine(");");
    }

    /// <summary>
    /// Builds the generated serialization source: file-scoped formatter classes and the registering module initializer.
    /// </summary>
    public string Build()
    {
        if (_registrations.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("#nullable disable");
        sb.AppendLine("#pragma warning disable CS0618, CS8019, CA2255");
        sb.Append(_classes);
        sb.Append("file static class __NexusSerializationRegistration_").AppendLine(_uniqueSuffix);
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        foreach (var registration in _registrations)
            sb.Append("        ").AppendLine(registration);
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("#pragma warning restore CS0618, CS8019, CA2255");
        sb.AppendLine("#nullable restore");
        return sb.ToString();
    }

    internal static ImmutableArray<SerializationDiagnostic> ToImmutable(IReadOnlyList<SerializationDiagnostic> list)
        => list.ToImmutableArray();
}

internal static class SyntaxFactsHelper
{
    public static bool IsKeyword(string name)
        => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None;
}
