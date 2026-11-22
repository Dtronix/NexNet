using System;

namespace NexNet.Serialization;

/// <summary>
/// Maps C# <c>long</c> and <c>ulong</c> to a checked JavaScript <c>number</c> instead of <c>bigint</c> in the generated
/// JavaScript client (D13). Applies to the <c>long</c>/<c>ulong</c> values of the member, parameter or return value it
/// is on, including inside <c>Nullable&lt;T&gt;</c>, sequences, sets, dictionaries, tuples and generic type arguments; on
/// the assembly, to every such value of every exported nexus and serialized type. Has no effect on C# or Java.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript value is a safe integer: writing a value beyond ±(2^53 − 1) (or a negative <c>ulong</c>) throws, and
/// reading one fails with a <c>range</c> serialization error. The wire format is unchanged.
/// </para>
/// <para>
/// Primitive arrays written as a whole (<c>long[]</c>, <c>List&lt;long&gt;</c>, <c>Memory&lt;long&gt;</c>) stay
/// <c>BigInt64Array</c>/<c>BigUint64Array</c>, and enums with a 64-bit underlying type stay <c>bigint</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [NexusObject]
/// public partial class Order
/// {
///     [NexusKey(0), NexusJsNumber] public long Id { get; set; }
/// }
///
/// public interface IOrderServer
/// {
///     [return: NexusJsNumber] ValueTask&lt;long&gt; Count([NexusJsNumber] long since);
/// }
///
/// // Or for the whole assembly:
/// [assembly: NexusJsNumber]
/// </code>
/// </example>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue,
    AllowMultiple = false,
    Inherited = false)]
public sealed class NexusJsNumberAttribute : Attribute
{
}
