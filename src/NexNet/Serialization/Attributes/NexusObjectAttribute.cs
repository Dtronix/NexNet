using System;

namespace NexNet.Serialization;

/// <summary>
/// Marks a class, struct, record or union base for NexNet MessagePack serialization.
/// Every public instance field and property must carry <see cref="NexusKeyAttribute"/> or <see cref="NexusIgnoreAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
public sealed class NexusObjectAttribute : Attribute
{
}

/// <summary>
/// Assigns the array position of a member. Keys start at 0 and should be dense.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class NexusKeyAttribute : Attribute
{
    /// <summary>
    /// Array position of the member.
    /// </summary>
    public int Key { get; }

    /// <summary>
    /// Creates the attribute.
    /// </summary>
    public NexusKeyAttribute(int key)
    {
        Key = key;
    }
}

/// <summary>
/// Excludes a member from serialization.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class NexusIgnoreAttribute : Attribute
{
}

/// <summary>
/// Selects the constructor used for deserialization.
/// </summary>
[AttributeUsage(AttributeTargets.Constructor)]
public sealed class NexusConstructorAttribute : Attribute
{
}

/// <summary>
/// Declares a union case on an abstract class or interface marked with <see cref="NexusObjectAttribute"/>.
/// A union value is written as <c>[tag, payload]</c>.
/// </summary>
/// <typeparam name="T">The union case type.</typeparam>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public sealed class NexusUnionAttribute<T> : Attribute
{
    /// <summary>
    /// Tag written on the wire for this case.
    /// </summary>
    public ushort Tag { get; }

    /// <summary>
    /// Creates the attribute.
    /// </summary>
    public NexusUnionAttribute(ushort tag)
    {
        Tag = tag;
    }
}

/// <summary>
/// Declares a type that must be serializable at runtime but does not appear in any nexus signature,
/// e.g. a closed generic used only with <c>CreateChannel&lt;T&gt;()</c>.
/// </summary>
/// <typeparam name="T">The type.</typeparam>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class NexusSerializableAttribute<T> : Attribute
{
}

/// <summary>
/// Registers a user formatter for a type that cannot be annotated.
/// </summary>
/// <typeparam name="TFormatter">Formatter type with a public parameterless constructor.</typeparam>
/// <typeparam name="T">The formatted type.</typeparam>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class NexusFormatterAttribute<TFormatter, T> : Attribute
    where TFormatter : NexusFormatter<T>, new()
{
}
