using System;
using System.Text.Json.Nodes;
using NexNet.Serialization;

namespace NexNet.Interop;

/// <summary>
/// Converts values of <typeparamref name="T"/> to and from typed JSON (<see cref="TypedJson"/>) through the registered
/// NexNet formatter of <typeparamref name="T"/>, so the JSON is exactly what the wire carries. Generated per type used by
/// a scaffolded nexus and registered in <see cref="TypedCodecs"/>.
/// </summary>
public sealed class TypedCodec<T>
{
    /// <summary>Creates a codec.</summary>
    /// <param name="schema">The shape of <typeparamref name="T"/>.</param>
    /// <param name="schemas">The declared types <paramref name="schema"/> refers to.</param>
    public TypedCodec(TypeSchema schema, SchemaSet schemas)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
    }

    /// <summary>The shape of <typeparamref name="T"/>.</summary>
    public TypeSchema Schema { get; }

    /// <summary>The declared types <see cref="Schema"/> refers to.</summary>
    public SchemaSet Schemas { get; }

    /// <summary>The typed JSON of <paramref name="value"/>.</summary>
    public JsonNode? Encode(T value) => TypedJson.FromMsgPack(NexusSerializer.Serialize(value), Schema, Schemas);

    /// <summary>The value of typed JSON <paramref name="node"/>.</summary>
    public T? Decode(JsonNode? node) => NexusSerializer.Deserialize<T>(TypedJson.ToMsgPack(node, Schema, Schemas));
}

/// <summary>
/// The typed-JSON codec of every type used by the scaffolded nexuses of the loaded assemblies, by type. Generated code
/// registers its codecs from a module initializer; the first registration of a type wins.
/// </summary>
public static class TypedCodecs
{
    private static class Cache<T>
    {
        public static TypedCodec<T>? Codec;
    }

    /// <summary>Registers <paramref name="codec"/> unless a codec of <typeparamref name="T"/> is already registered.</summary>
    public static void Register<T>(TypedCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        System.Threading.Interlocked.CompareExchange(ref Cache<T>.Codec, codec, null);
    }

    /// <summary>The registered codec of <typeparamref name="T"/>, if any.</summary>
    public static bool TryGet<T>(out TypedCodec<T>? codec)
    {
        codec = Cache<T>.Codec;
        return codec != null;
    }

    /// <summary>The registered codec of <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">No scaffolded nexus uses <typeparamref name="T"/>.</exception>
    public static TypedCodec<T> Get<T>()
        => Cache<T>.Codec ?? throw new InvalidOperationException(
            "No typed-JSON codec is registered for " + typeof(T) + ". Only types used by a [NexusTestScaffolding] nexus have one.");
}
