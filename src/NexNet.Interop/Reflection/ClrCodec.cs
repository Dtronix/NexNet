using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;

namespace NexNet.Interop.Reflection;

/// <summary>Typed-JSON encoders and decoders of runtime types, through <see cref="TypedCodecs"/>.</summary>
internal static class ClrCodec
{
    private sealed record Pair(Func<object?, JsonNode?> Encode, Func<JsonNode?, object?> Decode);

    private static readonly ConcurrentDictionary<Type, Pair> Cache = new();

    private static readonly MethodInfo CreateMethod =
        typeof(ClrCodec).GetMethod(nameof(Create), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static JsonNode? Encode(Type type, object? value) => Get(type).Encode(value);

    public static object? Decode(Type type, JsonNode? node) => Get(type).Decode(node);

    private static Pair Get(Type type) => Cache.GetOrAdd(type, static t => (Pair)CreateMethod.MakeGenericMethod(t).Invoke(null, null)!);

    private static Pair Create<T>()
    {
        var codec = TypedCodecs.Get<T>();
        return new Pair(
            value => codec.Encode((T)value!),
            node => node == null ? default(T) : codec.Decode(node));
    }
}
