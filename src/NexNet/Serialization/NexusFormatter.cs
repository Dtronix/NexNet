using System.Threading;

namespace NexNet.Serialization;

/// <summary>
/// Serializes and deserializes values of <typeparamref name="T"/> as MessagePack.
/// </summary>
/// <typeparam name="T">The handled type.</typeparam>
/// <remarks>
/// Implementations are stateless and thread-safe. User formatters for types that cannot be annotated are registered
/// with <c>[assembly: NexusFormatter&lt;TFormatter, T&gt;]</c> and must have a public parameterless constructor.
/// </remarks>
public abstract class NexusFormatter<T>
{
    /// <summary>
    /// Writes <paramref name="value"/> as exactly one MessagePack value.
    /// </summary>
    public abstract void Serialize(ref MsgPackWriter writer, T? value);

    /// <summary>
    /// Reads exactly one MessagePack value. Implementations may reuse <paramref name="value"/> when it is non-null
    /// and the type allows it (populate semantics).
    /// </summary>
    public abstract void Deserialize(ref MsgPackReader reader, ref T? value);
}

/// <summary>
/// Static per-type formatter slot used by code that only knows <typeparamref name="T"/> at runtime.
/// </summary>
public static class NexusFormatterCache<T>
{
    internal static NexusFormatter<T>? Formatter;
}

/// <summary>
/// Registration and lookup of formatters. Generated code registers formatters from a module initializer.
/// </summary>
public static class NexusFormatterRegistry
{
    private static int _initialized;

    /// <summary>
    /// Registers a formatter. The first registration for a type wins.
    /// </summary>
    public static void Register<T>(NexusFormatter<T> formatter)
    {
        Interlocked.CompareExchange(ref NexusFormatterCache<T>.Formatter, formatter, null);
    }

    /// <summary>
    /// Gets the registered formatter for <typeparamref name="T"/>, or throws.
    /// </summary>
    public static NexusFormatter<T> Get<T>()
    {
        return NexusFormatterCache<T>.Formatter ?? GetSlow<T>();
    }

    private static NexusFormatter<T> GetSlow<T>()
    {
        EnsureInitialized();
        return NexusFormatterCache<T>.Formatter ?? throw NexusSerializationException.MissingFormatter(typeof(T));
    }

    /// <summary>
    /// Returns true and the formatter if one is registered for <typeparamref name="T"/>.
    /// </summary>
    public static bool TryGet<T>(out NexusFormatter<T>? formatter)
    {
        formatter = NexusFormatterCache<T>.Formatter;
        if (formatter != null)
            return true;

        EnsureInitialized();
        formatter = NexusFormatterCache<T>.Formatter;
        return formatter != null;
    }

    /// <summary>
    /// Ensures the built-in formatters are registered. Called automatically by the NexNet module initializer.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 0)
            return;

        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            BuiltInFormatters.RegisterAll();
    }
}
