using System.Text;
using Microsoft.CodeAnalysis;

namespace NexNet.Generator;

internal class TypeHasher
{

    private Dictionary<ITypeSymbol, int> _hashCache = new(SymbolEqualityComparer.Default);
    
    public int GetHash(ITypeSymbol type)
    {
        if (!_hashCache.TryGetValue(type, out var hash))
        {
            _hashCache.Add(type, hash = GenerateHash(type));
        }

        return hash;
    }
    /// <summary>
    /// Traverses the graph of <see cref="ITypeSymbol"/> instances starting from a root type,
    /// collects a flat, ordered list of simple type representations for each encountered element,
    /// and returns them as strings.
    /// </summary>
    /// <param name="rootType">
    ///     The initial <see cref="ITypeSymbol"/> to inspect. Traversal explores its public instance
    ///     properties and fields (recursively), array element types, generic type arguments, and handles
    ///     nullable and array annotations appropriately.
    /// </param>
    /// <param name="b1"></param>
    /// <param name="includeRootType">True to add the root type into the list as the first item.</param>
    /// <returns>
    /// A <see cref="List{String}"/> containing the simple names of each visited type in the order
    /// they were encountered. Built-in special types are rendered immediately; complex types are
    /// queued for further inspection. Nullable and array markers (“?” and “[]”) are included as needed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Uses a depth-first search implemented with an explicit <see cref="Stack{ITypeSymbol}"/> and
    /// a <see cref="HashSet{ITypeSymbol}"/> to avoid revisiting symbols (preventing infinite loops
    /// on cyclic type graphs).  
    /// </para>
    /// <para>
    /// For user-defined types (non-System namespace), the method examines all public,
    /// non-static properties and fields.  
    /// It looks for an optional <c>[MemoryPackOrderAttribute(int)]</c> on each member to
    /// determine explicit ordering. If any member has the attribute, members are sorted
    /// by that order value; otherwise, they follow declaration order.  
    /// Each member’s type is then enqueued for further traversal.
    /// </para>
    /// "Compacts" any types that have been seen in the walking since if the type that has been seen previously or the member that is currently being walked is different, it would change the output.
    /// </remarks>
    public static List<string> Walk(ITypeSymbol rootType, bool includeRootType)
    {
        var props = new List<string>();
        // stack holds (node, setOfAncestors)
        var stack = new Stack<ITypeSymbol>();
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        stack.Push(rootType);

        var attrsCache = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        var unionCache = new Dictionary<AttributeData, int>();
        
        if(includeRootType)
            props.Add(GetSimpleType(rootType, false, -1, false));

        while (stack.Count > 0)
        {
            attrsCache.Clear();
            unionCache.Clear();
            var type = stack.Pop();

            // skip nulls or true cycles only
            if (type is null || !seen.Add(type))
                continue;
            
            if (type.TypeKind == TypeKind.Enum)
            {
                //props.Add(GetSimpleType(type, false, -1, false));

                var enums = type.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(f => f.ConstantValue != null)
                    .Select(f => (
                        Name: f.Name,
                        Value: Convert.ToInt64(f.ConstantValue)))
                    .OrderBy(f => f.Value);

                foreach (var e in enums)
                {
                    props.Add(e.Name + e.Value);
                }
                
                continue;
            }

            
            if (type.TypeKind == TypeKind.Interface)
            {
                var attributes = type.GetAttributes()
                    .Where(a => a.AttributeClass?.Name == "MemoryPackUnionAttribute").ToList();

                if (attributes.Count > 0)
                {
                    foreach (var attribute in attributes)
                    {
                        if (attribute.ConstructorArguments[0].Value is ushort order)
                            unionCache[attribute] = order;
                    }
                    
                    attributes.Sort((a, b) => unionCache[a] - unionCache[b]);

                    foreach (var attribute in attributes)
                    {
                        // MemoryPackUnionID
                        props.Add("MPUID" + unionCache[attribute]);
                        if (attribute.ConstructorArguments[1].Value is ITypeSymbol attributeReferencedType)
                        {
                            props.Add(GetSimpleType(attributeReferencedType, false, -1, false));
                            stack.Push(attributeReferencedType);
                        }
                    }       
                }

                attrsCache.Clear();
            }

            if (type.SpecialType != SpecialType.None)
            {
                props.Add(GetSimpleType(type, false, -1, false));
                continue;
            }

            if (type is IArrayTypeSymbol arr)
            {
                EnqueueType(arr.ElementType, stack, props, false, false, arr.Rank);
                continue;
            }

            if (type is INamedTypeSymbol named && named.Arity > 0)
            {
                var isNullable = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
                foreach (var ta in named.TypeArguments)
                {
                    EnqueueType(ta, stack, props, true, isNullable);
                }

                continue;
            }

            if (type.ContainingNamespace.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true)
                continue;

            // collect [MemoryPackOrder] or declaration order
            bool foundOrder = false;
            int counter = 0;
            foreach (var member in type.GetMembers())
            {
                if (member is IPropertySymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } prop)
                {
                    var attr = prop.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name == "MemoryPackOrderAttribute");
                    if (attr?.ConstructorArguments.Length > 0
                        && attr.ConstructorArguments[0].Value is int order)
                    {
                        attrsCache[prop] = order;
                        foundOrder = true;
                    }
                    else
                    {
                        attrsCache[prop] = counter;
                    }
                }
                else if (member is IFieldSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } field)
                {
                    var attr = field.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name == "MemoryPackOrderAttribute");
                    if (attr?.ConstructorArguments.Length > 0
                        && attr.ConstructorArguments[0].Value is int order)
                    {
                        attrsCache[field] = order;
                        foundOrder = true;
                    }
                    else
                    {
                        attrsCache[field] = counter;
                    }
                }

                counter++;
            }

            // sort if needed
            var membersToProcess = attrsCache.Keys.ToList();
            if (foundOrder)
                membersToProcess.Sort((a, b) => attrsCache[a] - attrsCache[b]);

            // enqueue each property’s type
            foreach (var member in membersToProcess)
            {
                if (member is IPropertySymbol propSymbol)
                    type = propSymbol.Type;
                else if (member is IFieldSymbol fieldSymbol)
                    type = fieldSymbol.Type;

                EnqueueType(type, stack, props, true, false);
            }
        }

        return props;

        static void EnqueueType(ITypeSymbol type,
            Stack<ITypeSymbol> stack,
            List<string> props,
            bool addType,
            bool forceNullable,
            int arrayRan = -1)
        {
            if (type is IArrayTypeSymbol
                {
                    ElementType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                } array
                && array.ElementType is INamedTypeSymbol namedType)
            {
                type = namedType.TypeArguments[0];
                props.Add(GetSimpleType(
                    type,
                    array.ElementNullableAnnotation == NullableAnnotation.Annotated,
                    array.Rank,
                    array.NullableAnnotation == NullableAnnotation.Annotated));
            }
            else if (addType && type is IArrayTypeSymbol stdArray)
            {
                props.Add(GetSimpleType(type, forceNullable, stdArray.Rank, false));
            }
            else if (addType && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
            {
                props.Add(GetSimpleType(type, forceNullable, arrayRan, false));
            }
            else if (IsNullable(type, out var nullableType))
            {
                type = nullableType!;
                props.Add(GetSimpleType(type, true, arrayRan, false));
            }

            if (type.SpecialType == SpecialType.None)
                stack.Push(type);
        }

        static bool IsNullable(ITypeSymbol type, out ITypeSymbol? nullableType)
        {
            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                && type is INamedTypeSymbol { Arity: 1 } namedType)
            {
                nullableType = namedType.TypeArguments[0];
                return true;
            }

            nullableType = null;
            return false;
        }

        static string GetSimpleType(ITypeSymbol type, bool forceNullable, int arrayRank, bool forceArrayNullable)
        {
            var arrayRankString = arrayRank > 0 ? $"[{new string(',', arrayRank - 1)}]" : "[]";
            if (arrayRank > 0 && forceNullable)
                return forceArrayNullable ? $"{type.Name}?{arrayRankString}?" : $"{type.Name}?{arrayRankString}";

            if (type is IArrayTypeSymbol arrayType)
            {
                if (arrayType.NullableAnnotation == NullableAnnotation.Annotated || forceNullable)
                    return arrayType.ElementNullableAnnotation == NullableAnnotation.Annotated
                        ? $"{arrayType.ElementType.Name}?{arrayRankString}?"
                        : $"{arrayType.ElementType.Name}{arrayRankString}?";

                return arrayType.ElementNullableAnnotation == NullableAnnotation.Annotated || forceNullable
                    ? $"{arrayType.ElementType.Name}?{arrayRankString}"
                    : $"{arrayType.ElementType.Name}{arrayRankString}";
            }

            return type.NullableAnnotation == NullableAnnotation.Annotated || forceNullable
                ? $"{type.Name}?"
                : $"{type.Name}";
        }
    }

    public static int GenerateHash(ITypeSymbol rootType)
    {
        var members = Walk(rootType, true);
        var stringHash = new XxHash32();
        var hash = new HashCode();
        foreach (var member in members)
        {
            hash.Add((int)stringHash.ComputeHash(Encoding.UTF8.GetBytes(member)));
        }

        return hash.ToHashCode();
    }
}
