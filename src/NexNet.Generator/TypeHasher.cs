using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;

namespace NexNet.Generator;

/// <summary>
/// Stack-allocated incremental hasher using FNV-1a algorithm.
/// Provides zero-allocation hashing for source generator use.
/// </summary>
internal ref struct IncrementalHasher
{
    private const uint FnvPrime = 16777619;
    private const uint FnvOffsetBasis = 2166136261;

    private uint _hash;

    public IncrementalHasher()
    {
        _hash = FnvOffsetBasis;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int value)
    {
        _hash ^= (uint)value;
        _hash *= FnvPrime;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(long value)
    {
        Add((int)value);
        Add((int)(value >> 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(byte value)
    {
        _hash ^= value;
        _hash *= FnvPrime;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(ushort value)
    {
        _hash ^= value;
        _hash *= FnvPrime;
    }

    /// <summary>
    /// Hash string without allocation by processing characters directly.
    /// </summary>
    public void AddString(string value)
    {
        foreach (char c in value)
        {
            _hash ^= c;
            _hash *= FnvPrime;
        }
        // Length distinguishes "ab" from sequential "a" + "b"
        Add(value.Length);
    }

    public readonly int ToHashCode() => (int)_hash;
}

/// <summary>
/// Result of type hashing containing the hash value and optional walk string.
/// </summary>
internal readonly struct TypeHashResult
{
    public int Hash { get; }
    public string? WalkString { get; }

    public TypeHashResult(int hash, string? walkString)
    {
        Hash = hash;
        WalkString = walkString;
    }
}

/// <summary>
/// High-performance type hasher that generates deterministic hashes for method parameters
/// reflecting the complete structure of MemoryPack-serialized types.
/// </summary>
/// <remarks>
/// Uses single-pass streaming with FNV-1a algorithm for efficient hashing.
/// Supports optional walk string generation for debugging/testing.
/// </remarks>
internal sealed class TypeHasher
{
    private readonly Dictionary<ITypeSymbol, TypeHashResult> _hashCache = new(SymbolEqualityComparer.Default);
    private readonly bool _generateWalkString;

    /// <summary>
    /// Creates a new TypeHasher instance.
    /// </summary>
    /// <param name="generateWalkString">
    /// When true, generates an indented string representation of the type walk.
    /// Default is false for performance.
    /// </param>
    public TypeHasher(bool generateWalkString = false)
    {
        _generateWalkString = generateWalkString;
    }

    /// <summary>
    /// Gets or computes the hash for a type symbol.
    /// Results are cached for repeated lookups.
    /// </summary>
    public int GetHash(ITypeSymbol type)
    {
        return GetHashResult(type).Hash;
    }

    /// <summary>
    /// Gets or computes the hash result including optional walk string.
    /// Results are cached for repeated lookups.
    /// </summary>
    public TypeHashResult GetHashResult(ITypeSymbol type)
    {
        if (!_hashCache.TryGetValue(type, out var result))
        {
            result = ComputeHash(type, _generateWalkString);
            _hashCache[type] = result;
        }
        return result;
    }

    /// <summary>
    /// Clears the hash cache. Call between compilation units if needed.
    /// </summary>
    public void ClearCache() => _hashCache.Clear();

    private static TypeHashResult ComputeHash(ITypeSymbol rootType, bool generateWalkString)
    {
        var hasher = new IncrementalHasher();
        // Only track MemoryPackable types in visited set - they're the only ones that can be self-referencing.
        // Primitive types like String, Int32, and CLR types cannot self-reference.
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var stack = new Stack<(ITypeSymbol type, int depth)>();
        StringBuilder? walkBuilder = generateWalkString ? new StringBuilder() : null;

        // Include root type name in hash
        hasher.AddString(rootType.Name);
        stack.Push((rootType, 0));

        while (stack.Count > 0)
        {
            var (type, depth) = stack.Pop();
            if (type is null)
                continue;

            // Only check visited for types that can self-reference (MemoryPackable types)
            // SpecialTypes (int, string, etc.), CLR types, and non-MemoryPackable types cannot self-reference
            bool canSelfReference = CanTypeSelfReference(type);

            if (canSelfReference)
            {
                bool alreadyVisited = !visited.Add(type);
                if (alreadyVisited)
                {
                    // Mark as seen in walk string but don't process again
                    if (walkBuilder != null)
                    {
                        AppendIndent(walkBuilder, depth);
                        walkBuilder.Append(type.Name);
                        walkBuilder.AppendLine(" [seen]");
                    }
                    continue;
                }
            }

            ProcessType(type, depth, ref hasher, stack, walkBuilder);
        }

        return new TypeHashResult(hasher.ToHashCode(), walkBuilder?.ToString());
    }

    /// <summary>
    /// Determines if a type can potentially self-reference (create cycles in the type graph).
    /// Only MemoryPackable types, enums (for consistency), and union interfaces can self-reference.
    /// Primitive types, CLR types, and non-MemoryPackable user types cannot.
    /// </summary>
    private static bool CanTypeSelfReference(ITypeSymbol type)
    {
        // SpecialTypes (int, string, bool, etc.) cannot self-reference
        if (type.SpecialType != SpecialType.None)
            return false;

        // Arrays cannot self-reference (their elements might, but that's handled separately)
        if (type is IArrayTypeSymbol)
            return false;

        // Nullable<T> cannot self-reference
        if (type is INamedTypeSymbol { Arity: 1 } named
            && type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return false;

        // System namespace types (CLR) cannot self-reference
        if (IsSystemNamespace(type))
            return false;

        // Only MemoryPackable types can self-reference through their members
        // Also include interfaces (for MemoryPackUnion) and enums
        if (type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.Enum)
            return true;

        return IsMemoryPackable(type);
    }

    private static void ProcessType(
        ITypeSymbol type,
        int depth,
        ref IncrementalHasher hasher,
        Stack<(ITypeSymbol type, int depth)> stack,
        StringBuilder? walkBuilder)
    {
        // Enum: hash field names and values
        if (type.TypeKind == TypeKind.Enum)
        {
            ProcessEnum(type, depth, ref hasher, walkBuilder);
            return;
        }

        // Interface with MemoryPackUnion
        if (type.TypeKind == TypeKind.Interface)
        {
            ProcessUnionInterface(type, depth, ref hasher, stack, walkBuilder);
            return;
        }

        // Built-in/SpecialTypes (CLR): hash with "_ST" prefix + enum value
        if (type.SpecialType != SpecialType.None)
        {
            hasher.AddString("_ST");
            hasher.Add((int)type.SpecialType);

            if (walkBuilder != null)
            {
                AppendIndent(walkBuilder, depth);
                walkBuilder.Append(type.Name);
                if (type.NullableAnnotation == NullableAnnotation.Annotated)
                    walkBuilder.Append('?');
                walkBuilder.AppendLine(" [SpecialType]");
            }
            return;
        }

        // Array: hash rank and nullability, recurse element
        if (type is IArrayTypeSymbol arr)
        {
            hasher.Add(arr.Rank);
            hasher.Add((byte)(arr.NullableAnnotation == NullableAnnotation.Annotated ? 1 : 0));

            // Check if element is Nullable<T> and unwrap for display and hashing
            var elemType = arr.ElementType;
            bool elementIsNullableT = elemType is INamedTypeSymbol { Arity: 1 }
                && elemType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

            if (elementIsNullableT)
            {
                var innerType = ((INamedTypeSymbol)elemType).TypeArguments[0];
                hasher.AddString(innerType.Name);
                hasher.AddString("?");
            }
            else
            {
                hasher.Add((byte)(arr.ElementNullableAnnotation == NullableAnnotation.Annotated ? 1 : 0));
            }

            if (walkBuilder != null)
            {
                AppendIndent(walkBuilder, depth);
                if (elementIsNullableT)
                {
                    var innerType = ((INamedTypeSymbol)elemType).TypeArguments[0];
                    walkBuilder.Append(innerType.Name);
                    walkBuilder.Append('?');
                }
                else
                {
                    walkBuilder.Append(elemType.Name);
                    if (arr.ElementNullableAnnotation == NullableAnnotation.Annotated)
                        walkBuilder.Append('?');
                }
                walkBuilder.Append('[');
                walkBuilder.Append(',', arr.Rank - 1);
                walkBuilder.Append(']');
                if (arr.NullableAnnotation == NullableAnnotation.Annotated)
                    walkBuilder.Append('?');
                walkBuilder.AppendLine(" [Array]");
            }

            stack.Push((elemType, depth + 1));
            return;
        }

        // Nullable<T>: treat as T? - unwrap and hash inner type with nullable marker
        if (type is INamedTypeSymbol { Arity: 1 } nullableNamed
            && type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            var innerType = nullableNamed.TypeArguments[0];
            // Hash inner type name + nullable marker
            hasher.AddString(innerType.Name);
            hasher.AddString("?");

            if (walkBuilder != null)
            {
                AppendIndent(walkBuilder, depth);
                walkBuilder.Append(innerType.Name);
                walkBuilder.AppendLine("? [Nullable]");
            }

            // Recurse into the inner type
            stack.Push((innerType, depth + 1));
            return;
        }

        // Generic types: hash name + arity, then recurse type arguments
        if (type is INamedTypeSymbol { Arity: > 0 } named)
        {
            hasher.AddString(named.Name);
            hasher.Add(named.Arity);

            if (walkBuilder != null)
            {
                AppendIndent(walkBuilder, depth);
                walkBuilder.Append(named.Name);
                walkBuilder.Append('<');
                walkBuilder.Append(named.Arity);
                walkBuilder.Append('>');
                if (type.NullableAnnotation == NullableAnnotation.Annotated)
                    walkBuilder.Append('?');
                if (IsSystemNamespace(type))
                    walkBuilder.Append(" [CLR]");
                else if (IsMemoryPackable(type))
                    walkBuilder.Append(" [MemoryPackable]");
                walkBuilder.AppendLine();
            }

            // Push type arguments in reverse order so they process in correct order
            for (int i = named.TypeArguments.Length - 1; i >= 0; i--)
            {
                var ta = named.TypeArguments[i];
                hasher.AddString(ta.Name);
                hasher.Add((byte)(ta.NullableAnnotation == NullableAnnotation.Annotated ? 1 : 0));
                stack.Push((ta, depth + 1));
            }
            return;
        }

        // System namespace types (CLR): hash name only, do NOT walk members
        if (IsSystemNamespace(type))
        {
            hasher.AddString(type.Name);

            if (walkBuilder != null)
            {
                AppendIndent(walkBuilder, depth);
                walkBuilder.Append(type.Name);
                if (type.NullableAnnotation == NullableAnnotation.Annotated)
                    walkBuilder.Append('?');
                walkBuilder.AppendLine(" [CLR]");
            }
            return;
        }

        // Non-MemoryPackable user types: treat as CLR (name only)
        if (!IsMemoryPackable(type))
        {
            hasher.AddString(type.Name);

            if (walkBuilder != null)
            {
                AppendIndent(walkBuilder, depth);
                walkBuilder.Append(type.Name);
                if (type.NullableAnnotation == NullableAnnotation.Annotated)
                    walkBuilder.Append('?');
                walkBuilder.AppendLine(" [NotMemoryPackable]");
            }
            return;
        }

        // MemoryPackable user-defined types: walk members in order
        ProcessUserType(type, depth, ref hasher, stack, walkBuilder);
    }

    /// <summary>
    /// Checks if a type has the [MemoryPackable] attribute.
    /// </summary>
    private static bool IsMemoryPackable(ITypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "MemoryPackableAttribute")
                return true;
        }
        return false;
    }

    private static void ProcessEnum(
        ITypeSymbol type,
        int depth,
        ref IncrementalHasher hasher,
        StringBuilder? walkBuilder)
    {
        var members = type.GetMembers();
        int fieldCount = 0;

        foreach (var m in members)
        {
            if (m is IFieldSymbol { ConstantValue: not null })
                fieldCount++;
        }

        if (walkBuilder != null)
        {
            AppendIndent(walkBuilder, depth);
            walkBuilder.Append(type.Name);
            walkBuilder.AppendLine(" [Enum]");
        }

        if (fieldCount == 0)
            return;

        var fields = new (string name, int nameHash, long value)[fieldCount];
        int idx = 0;

        foreach (var member in members)
        {
            if (member is IFieldSymbol { ConstantValue: not null } field)
            {
                fields[idx++] = (field.Name, field.Name.GetHashCode(), Convert.ToInt64(field.ConstantValue));
            }
        }

        Array.Sort(fields, (a, b) => a.value.CompareTo(b.value));

        foreach (var (name, nameHash, value) in fields)
        {
            hasher.Add(nameHash);
            hasher.Add(value);

            if (walkBuilder != null)
            {
                AppendIndent(walkBuilder, depth + 1);
                walkBuilder.Append(name);
                walkBuilder.Append(" = ");
                walkBuilder.AppendLine(value.ToString());
            }
        }
    }

    private static void ProcessUnionInterface(
        ITypeSymbol type,
        int depth,
        ref IncrementalHasher hasher,
        Stack<(ITypeSymbol type, int depth)> stack,
        StringBuilder? walkBuilder)
    {
        var attrs = type.GetAttributes();
        var unions = new List<(ushort order, ITypeSymbol type)>();

        foreach (var attr in attrs)
        {
            if (attr.AttributeClass?.Name != "MemoryPackUnionAttribute")
                continue;

            if (attr.ConstructorArguments.Length >= 2
                && attr.ConstructorArguments[0].Value is ushort order
                && attr.ConstructorArguments[1].Value is ITypeSymbol unionType)
            {
                unions.Add((order, unionType));
            }
        }

        if (walkBuilder != null)
        {
            AppendIndent(walkBuilder, depth);
            walkBuilder.Append(type.Name);
            walkBuilder.Append(" [MemoryPackUnion");
            if (unions.Count > 0)
            {
                walkBuilder.Append(':');
                walkBuilder.Append(unions.Count);
            }
            walkBuilder.AppendLine("]");
        }

        if (unions.Count == 0)
            return;

        unions.Sort((a, b) => a.order.CompareTo(b.order));

        // Output in forward order for walk string
        foreach (var (order, unionType) in unions)
        {
            hasher.Add(order);
            hasher.AddString(unionType.Name);

            if (walkBuilder != null)
            {
                AppendIndent(walkBuilder, depth + 1);
                walkBuilder.Append("[MPUID:");
                walkBuilder.Append(order);
                walkBuilder.Append("] ");
                walkBuilder.AppendLine(unionType.Name);
            }
        }

        // Push to stack in reverse order so they process in forward order
        for (int i = unions.Count - 1; i >= 0; i--)
        {
            stack.Push((unions[i].type, depth + 2));
        }
    }

    private static void ProcessUserType(
        ITypeSymbol type,
        int depth,
        ref IncrementalHasher hasher,
        Stack<(ITypeSymbol type, int depth)> stack,
        StringBuilder? walkBuilder)
    {
        if (walkBuilder != null)
        {
            AppendIndent(walkBuilder, depth);
            walkBuilder.Append(type.Name);
            if (type.NullableAnnotation == NullableAnnotation.Annotated)
                walkBuilder.Append('?');
            walkBuilder.AppendLine(" [MemoryPackable]");
        }

        var members = type.GetMembers();
        var ordered = new List<(int order, string name, ITypeSymbol type, NullableAnnotation nullable)>();
        bool hasExplicitOrder = false;
        int declOrder = 0;

        foreach (var member in members)
        {
            ITypeSymbol? memberType = null;
            NullableAnnotation nullable = NullableAnnotation.None;
            int order = declOrder;
            string? memberName = null;

            if (member is IPropertySymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } prop)
            {
                memberType = prop.Type;
                nullable = prop.NullableAnnotation;
                memberName = prop.Name;
                order = GetMemoryPackOrder(prop, declOrder, ref hasExplicitOrder);
            }
            else if (member is IFieldSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } field)
            {
                memberType = field.Type;
                nullable = field.NullableAnnotation;
                memberName = field.Name;
                order = GetMemoryPackOrder(field, declOrder, ref hasExplicitOrder);
            }

            if (memberType != null && memberName != null)
            {
                ordered.Add((order, memberName, memberType, nullable));
            }
            declOrder++;
        }

        if (hasExplicitOrder)
        {
            ordered.Sort((a, b) => a.order.CompareTo(b.order));
        }

        // Process in forward order for correct output, then push to stack in reverse
        foreach (var (order, name, memberType, nullable) in ordered)
        {
            hasher.AddString(memberType.Name);
            hasher.Add((byte)(nullable == NullableAnnotation.Annotated ? 1 : 0));

            // Include structural info
            if (memberType is IArrayTypeSymbol arr)
            {
                hasher.Add(arr.Rank);
            }
            else if (memberType is INamedTypeSymbol { Arity: > 0 } named)
            {
                hasher.Add(named.Arity);
            }

            if (walkBuilder != null)
            {
                AppendIndent(walkBuilder, depth + 1);
                walkBuilder.Append(name);
                walkBuilder.Append(": ");
                AppendTypeName(walkBuilder, memberType, nullable);
                if (hasExplicitOrder)
                {
                    walkBuilder.Append(" [Order:");
                    walkBuilder.Append(order);
                    walkBuilder.Append(']');
                }
                walkBuilder.AppendLine();
            }
        }

        // Push to stack in reverse order so they process in forward order
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            var (_, _, memberType, _) = ordered[i];
            if (memberType.SpecialType == SpecialType.None)
                stack.Push((memberType, depth + 2));
        }
    }

    private static void AppendTypeName(StringBuilder sb, ITypeSymbol type, NullableAnnotation nullable)
    {
        if (type is IArrayTypeSymbol arr)
        {
            // For arrays, check if element is Nullable<T> and unwrap
            var elemType = arr.ElementType;
            if (elemType is INamedTypeSymbol { Arity: 1 } nullableElem
                && elemType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                sb.Append(nullableElem.TypeArguments[0].Name);
                sb.Append('?');
            }
            else
            {
                sb.Append(elemType.Name);
                if (arr.ElementNullableAnnotation == NullableAnnotation.Annotated)
                    sb.Append('?');
            }
            sb.Append('[');
            sb.Append(',', arr.Rank - 1);
            sb.Append(']');
            if (nullable == NullableAnnotation.Annotated)
                sb.Append('?');
        }
        // Nullable<T>: show as InnerType?
        else if (type is INamedTypeSymbol { Arity: 1 } nullableNamed
            && type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            sb.Append(nullableNamed.TypeArguments[0].Name);
            sb.Append('?');
        }
        else if (type is INamedTypeSymbol { Arity: > 0 } named)
        {
            sb.Append(named.Name);
            sb.Append('<');
            sb.Append(named.Arity);
            sb.Append('>');
            if (nullable == NullableAnnotation.Annotated)
                sb.Append('?');
        }
        else
        {
            sb.Append(type.Name);
            if (nullable == NullableAnnotation.Annotated)
                sb.Append('?');
        }
    }

    private static int GetMemoryPackOrder(ISymbol member, int defaultOrder, ref bool hasExplicit)
    {
        foreach (var attr in member.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "MemoryPackOrderAttribute"
                && attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is int order)
            {
                hasExplicit = true;
                return order;
            }
        }
        return defaultOrder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSystemNamespace(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
            return false;

        while (ns.ContainingNamespace is { IsGlobalNamespace: false })
            ns = ns.ContainingNamespace;

        return ns.Name == "System";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendIndent(StringBuilder sb, int depth)
    {
        for (int i = 0; i < depth; i++)
        {
            sb.Append("  ");
        }
    }
}
