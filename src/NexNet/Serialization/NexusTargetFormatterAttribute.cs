using System;

namespace NexNet.Serialization;

/// <summary>
/// Maps a type that has a user formatter (<see cref="NexusFormatterAttribute{TFormatter, T}"/>) to a hand-written
/// formatter in another client language. Without a mapping, a nexus that uses the type cannot be generated for that
/// language.
/// </summary>
/// <remarks>
/// The generator emits the native formatter interface for the type; the native class named here implements it and
/// must write exactly the bytes the C# formatter writes. Declare one attribute per (type, language) pair.
/// </remarks>
/// <example>
/// <code>
/// [assembly: NexusTargetFormatter&lt;Money&gt;(NexusTargets.Java, "com.example.MoneyFormatter")]
/// [assembly: NexusTargetFormatter&lt;Money&gt;(NexusTargets.JavaScript, "./money-formatter.js#MoneyFormatter")]
/// </code>
/// </example>
/// <typeparam name="T">The formatted .NET type.</typeparam>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
// ReSharper disable once UnusedTypeParameter
public sealed class NexusTargetFormatterAttribute<T> : Attribute
{
    /// <summary>
    /// Language the formatter is for. Combined flags apply the same name to each language.
    /// </summary>
    public NexusTargets Target { get; }

    /// <summary>
    /// Fully qualified name of the native formatter class.
    /// </summary>
    public string NativeFormatter { get; }

    /// <summary>
    /// Creates the attribute.
    /// </summary>
    /// <param name="target">Language the formatter is for.</param>
    /// <param name="nativeFormatterFqcn">Fully qualified name of the native formatter class.</param>
    public NexusTargetFormatterAttribute(NexusTargets target, string nativeFormatterFqcn)
    {
        Target = target;
        NativeFormatter = nativeFormatterFqcn;
    }
}
