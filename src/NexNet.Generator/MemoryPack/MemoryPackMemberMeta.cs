using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexNet.Generator.MemoryPack;

internal class MemoryPackMemberMeta
{
    public ISymbol Symbol { get; }
    //public string Name { get; }
    public ITypeSymbol MemberType { get; }
    //public INamedTypeSymbol? CustomFormatter { get; }
    //public string? CustomFormatterName { get; }
    //public bool IsField { get; }
    //public bool IsProperty { get; }
    //public bool IsSettable { get; }
    //public bool IsAssignable { get; }
    public int Order { get; }
    //public bool HasExplicitOrder { get; }
    public MemberKind Kind { get; }
    //public bool SuppressDefaultInitialization { get; }

    MemoryPackMemberMeta(int order)
    {
        this.Symbol = null!;
        //this.Name = null!;
        this.MemberType = null!;
        this.Order = order;
        //this.Kind = MemberKind.Blank;
    }

    public MemoryPackMemberMeta(ISymbol symbol, MemoryPackReferences references, int sequentialOrder)
    {
        this.Symbol = symbol;
        //this.Name = symbol.Name;
        this.Order = sequentialOrder;
        //this.SuppressDefaultInitialization = symbol.ContainsAttribute(references.SkipOverwriteDefaultAttribute);
        var orderAttr = symbol.GetAttribute(references.MemoryPackOrderAttribute);
        if (orderAttr != null)
        {
            this.Order = (int)(orderAttr.ConstructorArguments[0].Value ?? sequentialOrder);
            //this.HasExplicitOrder = true;

        }
        else
        {
            //this.HasExplicitOrder = false;
        }

        if (symbol is IFieldSymbol f)
        {
            //IsProperty = false;
            //IsField = true;
            //IsSettable = !f.IsReadOnly; // readonly field can not set.
            //IsAssignable = IsSettable && !f.IsRequired
            MemberType = f.Type;
        }
        else if (symbol is IPropertySymbol p)
        {
            //IsProperty = true;
            //IsField = false;
            //IsSettable = !p.IsReadOnly;
            //IsAssignable = IsSettable && !p.IsRequired && (p.SetMethod != null && !p.SetMethod.IsInitOnly);
            MemberType = p.Type;
        }
        else
        {
            throw new Exception("member is not field or property.");
        }
        /*
        if (references.MemoryPackCustomFormatterAttribute != null)
        {
            var genericFormatter = false;
            var customFormatterAttr = symbol.GetImplAttribute(references.MemoryPackCustomFormatterAttribute);
            if (customFormatterAttr == null && references.MemoryPackCustomFormatter2Attribute != null)
            {
                customFormatterAttr = symbol.GetImplAttribute(references.MemoryPackCustomFormatter2Attribute);
                genericFormatter = true;
            }

            if (customFormatterAttr != null)
            {
                CustomFormatter = customFormatterAttr.AttributeClass!;
                //Kind = MemberKind.CustomFormatter;

                string formatterName;
                if (genericFormatter)
                {
                    formatterName = CustomFormatter.GetAllBaseTypes().First(x => x.EqualsUnconstructedGenericType(references.MemoryPackCustomFormatter2Attribute!))
                        .TypeArguments[0].FullyQualifiedToString();
                }
                else
                {
                    formatterName = $"IMemoryPackFormatter<{MemberType.FullyQualifiedToString()}>";
                }
                //CustomFormatterName = formatterName;
                return;
            }
        }
        */
        Kind = ParseMemberKind(symbol, MemberType, references);
    }

    public static MemoryPackMemberMeta CreateEmpty(int order)
    {
        return new MemoryPackMemberMeta(order);
    }

    public Location GetLocation(TypeDeclarationSyntax fallback)
    {
        var location = Symbol.Locations.FirstOrDefault() ?? fallback.Identifier.GetLocation();
        return location;
    }
    
    static MemberKind ParseMemberKind(ISymbol? memberSymbol, ITypeSymbol memberType, MemoryPackReferences references)
    {
        if (memberType.SpecialType is SpecialType.System_Object or SpecialType.System_Array or SpecialType.System_Delegate or SpecialType.System_MulticastDelegate || memberType.TypeKind == TypeKind.Delegate)
        {
            return MemberKind.NonSerializable; // object, Array, delegate is not allowed
        }

        if (memberType.TypeKind == TypeKind.Enum)
        {
            return MemberKind.Enum;
        }

        if (memberType.IsUnmanagedType)
        {
            if (memberType is INamedTypeSymbol unmanagedNts)
            {
                if (unmanagedNts.IsRefLikeType)
                {
                    return MemberKind.RefLike;
                }
                if (unmanagedNts.EqualsUnconstructedGenericType(references.KnownTypes.System_Nullable_T))
                {
                    // unamanged nullable<T> can not pass to where T:unmanaged constraint
                    if (unmanagedNts.TypeArguments[0].IsUnmanagedType)
                    {
                        return MemberKind.UnmanagedNullable;
                    }

                    return MemberKind.Nullable;
                }
            }

            return MemberKind.Unmanaged;
        }

        if (memberType.SpecialType == SpecialType.System_String)
        {
            return MemberKind.String;
        }

        if (memberType.AllInterfaces.Any(x => x.EqualsUnconstructedGenericType(references.IMemoryPackable)))
        {
            return MemberKind.MemoryPackable;
        }

        if (memberType.TryGetMemoryPackableType(references, out var generateType, out _))
        {
            switch (generateType)
            {
                case GenerateType.Object:
                case GenerateType.VersionTolerant:
                case GenerateType.CircularReference:
                    return MemberKind.MemoryPackable;
                case GenerateType.Collection:
                    return MemberKind.MemoryPackableCollection;
                case GenerateType.Union:
                    return MemberKind.MemoryPackableUnion;
                case GenerateType.NoGenerate:
                default:
                    return MemberKind.MemoryPackableNoGenerate;
            }
        }

        if (memberType.IsWillImplementMemoryPackUnion(references))
        {
            return MemberKind.MemoryPackUnion;
        }

        if (memberType.TypeKind == TypeKind.Array)
        {
            if (memberType is IArrayTypeSymbol array)
            {
                if (array.IsSZArray)
                {
                    var elemType = array.ElementType;
                    if (elemType.IsUnmanagedType)
                    {
                        if (elemType is INamedTypeSymbol unmanagedNts && unmanagedNts.EqualsUnconstructedGenericType(references.KnownTypes.System_Nullable_T))
                        {
                            // T?[] can not use Write/ReadUnmanagedArray
                            return MemberKind.Array;
                        }

                        return MemberKind.UnmanagedArray;
                    }

                    if (elemType.TryGetMemoryPackableType(references, out var elemGenerateType, out _) && elemGenerateType is GenerateType.Object or GenerateType.VersionTolerant or GenerateType.CircularReference)
                    {
                        return MemberKind.MemoryPackableArray;
                    }

                    return MemberKind.Array;
                }

                // allows 2, 3, 4
                if (array.Rank <= 4)
                {
                    return MemberKind.Object;
                }
            }

            return MemberKind.NonSerializable;
        }

        if (memberType.TypeKind == TypeKind.TypeParameter) // T
        {
            return MemberKind.Object;
        }

        // or non unmanaged type
        if (memberType is INamedTypeSymbol nts)
        {
            if (nts.IsRefLikeType)
            {
                return MemberKind.RefLike;
            }
            if (nts.EqualsUnconstructedGenericType(references.KnownTypes.System_Nullable_T))
            {
                return MemberKind.Nullable;
            }

            if (nts.EqualsUnconstructedGenericType(references.KnownTypes.System_Collections_Generic_List_T))
            {
                if (nts.TypeArguments[0].TryGetMemoryPackableType(references, out var elemGenerateType, out _) && elemGenerateType is GenerateType.Object or GenerateType.VersionTolerant or GenerateType.CircularReference)
                {
                    return MemberKind.MemoryPackableList;
                }
                return MemberKind.KnownType;
            }
        }

        if (references.KnownTypes.Contains(memberType, out _))
        {
            return MemberKind.KnownType;
        }

        if (memberSymbol != null)
        {
            if (memberSymbol.ContainsAttribute(references.MemoryPackAllowSerializeAttribute))
            {
                return MemberKind.AllowSerialize;
            }
        }

        return MemberKind.NonSerializable; // maybe can't serialize, diagnostics target
    }
}
