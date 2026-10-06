using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NexNet.Serialization.Formatters;

#pragma warning disable CS1591 // Formatter members are self-describing.

/// <summary>
/// Base for formatters that delegate to an element formatter resolved lazily from the registry, so
/// registration order inside module initializers does not matter.
/// </summary>
public abstract class ElementFormatterBase<TCollection, TElement> : NexusFormatter<TCollection>
{
    private NexusFormatter<TElement>? _element;

    protected ElementFormatterBase()
    {
    }

    protected ElementFormatterBase(NexusFormatter<TElement> element)
    {
        _element = element;
    }

    protected NexusFormatter<TElement> Element
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _element ??= NexusFormatterRegistry.Get<TElement>();
    }
}

public sealed class NullableFormatter<T> : NexusFormatter<T?>
    where T : struct
{
    private NexusFormatter<T>? _inner;

    public NullableFormatter()
    {
    }

    public NullableFormatter(NexusFormatter<T> inner)
    {
        _inner = inner;
    }

    private NexusFormatter<T> Inner => _inner ??= NexusFormatterRegistry.Get<T>();

    public override void Serialize(ref MsgPackWriter writer, T? value)
    {
        if (!value.HasValue)
        {
            writer.WriteNil();
            return;
        }

        Inner.Serialize(ref writer, value.Value);
    }

    public override void Deserialize(ref MsgPackReader reader, ref T? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        T inner = default;
        Inner.Deserialize(ref reader, ref inner);
        value = inner;
    }
}

public sealed class ArrayFormatter<T> : ElementFormatterBase<T[], T>
{
    public ArrayFormatter() { }
    public ArrayFormatter(NexusFormatter<T> element) : base(element) { }

    public override void Serialize(ref MsgPackWriter writer, T[]? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        var element = Element;
        writer.WriteArrayHeader(value.Length);
        for (var i = 0; i < value.Length; i++)
            element.Serialize(ref writer, value[i]);
    }

    public override void Deserialize(ref MsgPackReader reader, ref T[]? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        var count = reader.ReadArrayHeader();
        reader.Enter();
        var element = Element;
        var result = count == 0 ? Array.Empty<T>() : new T[count];
        for (var i = 0; i < count; i++)
            element.Deserialize(ref reader, ref result[i]!);
        reader.Exit();
        value = result;
    }
}

public sealed class ListFormatter<T> : ElementFormatterBase<List<T>, T>
{
    public ListFormatter() { }
    public ListFormatter(NexusFormatter<T> element) : base(element) { }

    public override void Serialize(ref MsgPackWriter writer, List<T>? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        var element = Element;
        var span = CollectionsMarshal.AsSpan(value);
        writer.WriteArrayHeader(span.Length);
        for (var i = 0; i < span.Length; i++)
            element.Serialize(ref writer, span[i]);
    }

    public override void Deserialize(ref MsgPackReader reader, ref List<T>? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        var count = reader.ReadArrayHeader();
        reader.Enter();
        var element = Element;
        var list = new List<T>(count);
        CollectionsMarshal.SetCount(list, count);
        var span = CollectionsMarshal.AsSpan(list);
        for (var i = 0; i < count; i++)
            element.Deserialize(ref reader, ref span[i]!);
        reader.Exit();
        value = list;
    }
}

/// <summary>
/// Serializes any sequence interface as an array; deserializes into <c>List&lt;T&gt;</c>.
/// Used for <c>IEnumerable&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>,
/// <c>IReadOnlyCollection&lt;T&gt;</c> and <c>IReadOnlyList&lt;T&gt;</c>.
/// </summary>
public sealed class InterfaceListFormatter<TInterface, T> : ElementFormatterBase<TInterface, T>
    where TInterface : class, IEnumerable<T>
{
    public InterfaceListFormatter() { }
    public InterfaceListFormatter(NexusFormatter<T> element) : base(element) { }

    public override void Serialize(ref MsgPackWriter writer, TInterface? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        var element = Element;
        if (value is ICollection<T> collection)
        {
            writer.WriteArrayHeader(collection.Count);
            foreach (var item in collection)
                element.Serialize(ref writer, item);
            return;
        }

        if (value is IReadOnlyCollection<T> readOnly)
        {
            writer.WriteArrayHeader(readOnly.Count);
            foreach (var item in readOnly)
                element.Serialize(ref writer, item);
            return;
        }

        var materialized = new List<T>(value);
        writer.WriteArrayHeader(materialized.Count);
        foreach (var item in materialized)
            element.Serialize(ref writer, item);
    }

    public override void Deserialize(ref MsgPackReader reader, ref TInterface? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        var count = reader.ReadArrayHeader();
        reader.Enter();
        var element = Element;
        var list = new List<T>(count);
        CollectionsMarshal.SetCount(list, count);
        var span = CollectionsMarshal.AsSpan(list);
        for (var i = 0; i < count; i++)
            element.Deserialize(ref reader, ref span[i]!);
        reader.Exit();
        value = (TInterface)(object)list;
    }
}

public sealed class HashSetFormatter<T> : ElementFormatterBase<HashSet<T>, T>
{
    public HashSetFormatter() { }
    public HashSetFormatter(NexusFormatter<T> element) : base(element) { }

    public override void Serialize(ref MsgPackWriter writer, HashSet<T>? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        var element = Element;
        writer.WriteArrayHeader(value.Count);
        foreach (var item in value)
            element.Serialize(ref writer, item);
    }

    public override void Deserialize(ref MsgPackReader reader, ref HashSet<T>? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        var count = reader.ReadArrayHeader();
        reader.Enter();
        var element = Element;
        var set = new HashSet<T>(count, CollectionComparers.Get<T>(reader.Options));
        for (var i = 0; i < count; i++)
        {
            T item = default!;
            element.Deserialize(ref reader, ref item!);
            set.Add(item);
        }

        reader.Exit();
        value = set;
    }
}

public sealed class QueueFormatter<T> : ElementFormatterBase<Queue<T>, T>
{
    public QueueFormatter() { }
    public QueueFormatter(NexusFormatter<T> element) : base(element) { }

    public override void Serialize(ref MsgPackWriter writer, Queue<T>? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        var element = Element;
        writer.WriteArrayHeader(value.Count);
        foreach (var item in value)
            element.Serialize(ref writer, item);
    }

    public override void Deserialize(ref MsgPackReader reader, ref Queue<T>? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        var count = reader.ReadArrayHeader();
        reader.Enter();
        var element = Element;
        var queue = new Queue<T>(count);
        for (var i = 0; i < count; i++)
        {
            T item = default!;
            element.Deserialize(ref reader, ref item!);
            queue.Enqueue(item);
        }

        reader.Exit();
        value = queue;
    }
}

/// <summary>
/// <see cref="Stack{T}"/> written top-first; reading pushes in reverse so the order round-trips.
/// </summary>
public sealed class StackFormatter<T> : ElementFormatterBase<Stack<T>, T>
{
    public StackFormatter() { }
    public StackFormatter(NexusFormatter<T> element) : base(element) { }

    public override void Serialize(ref MsgPackWriter writer, Stack<T>? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        var element = Element;
        writer.WriteArrayHeader(value.Count);
        foreach (var item in value)
            element.Serialize(ref writer, item);
    }

    public override void Deserialize(ref MsgPackReader reader, ref Stack<T>? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        var count = reader.ReadArrayHeader();
        reader.Enter();
        var element = Element;
        var items = new T[count];
        for (var i = 0; i < count; i++)
            element.Deserialize(ref reader, ref items[i]!);
        reader.Exit();

        var stack = new Stack<T>(count);
        for (var i = count - 1; i >= 0; i--)
            stack.Push(items[i]);
        value = stack;
    }
}

public sealed class DictionaryFormatter<TKey, TValue> : NexusFormatter<Dictionary<TKey, TValue>>
    where TKey : notnull
{
    private NexusFormatter<TKey>? _key;
    private NexusFormatter<TValue>? _value;

    private NexusFormatter<TKey> KeyFormatter => _key ??= NexusFormatterRegistry.Get<TKey>();
    private NexusFormatter<TValue> ValueFormatter => _value ??= NexusFormatterRegistry.Get<TValue>();

    public override void Serialize(ref MsgPackWriter writer, Dictionary<TKey, TValue>? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        DictionaryCore.Serialize(ref writer, value, value.Count, KeyFormatter, ValueFormatter);
    }

    public override void Deserialize(ref MsgPackReader reader, ref Dictionary<TKey, TValue>? value)
    {
        value = DictionaryCore.Deserialize(ref reader, KeyFormatter, ValueFormatter);
    }
}

/// <summary>
/// Serializes dictionary interfaces as a map; deserializes into <c>Dictionary&lt;TKey, TValue&gt;</c>.
/// </summary>
public sealed class InterfaceDictionaryFormatter<TInterface, TKey, TValue> : NexusFormatter<TInterface>
    where TInterface : class, IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private NexusFormatter<TKey>? _key;
    private NexusFormatter<TValue>? _value;

    private NexusFormatter<TKey> KeyFormatter => _key ??= NexusFormatterRegistry.Get<TKey>();
    private NexusFormatter<TValue> ValueFormatter => _value ??= NexusFormatterRegistry.Get<TValue>();

    public override void Serialize(ref MsgPackWriter writer, TInterface? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        int count = value switch
        {
            ICollection<KeyValuePair<TKey, TValue>> c => c.Count,
            IReadOnlyCollection<KeyValuePair<TKey, TValue>> r => r.Count,
            _ => -1
        };

        if (count < 0)
        {
            var materialized = new List<KeyValuePair<TKey, TValue>>(value);
            DictionaryCore.Serialize(ref writer, materialized, materialized.Count, KeyFormatter, ValueFormatter);
            return;
        }

        DictionaryCore.Serialize(ref writer, value, count, KeyFormatter, ValueFormatter);
    }

    public override void Deserialize(ref MsgPackReader reader, ref TInterface? value)
    {
        var dictionary = DictionaryCore.Deserialize(ref reader, KeyFormatter, ValueFormatter);
        value = (TInterface?)(object?)dictionary;
    }
}

internal static class DictionaryCore
{
    public static void Serialize<TKey, TValue>(
        ref MsgPackWriter writer,
        IEnumerable<KeyValuePair<TKey, TValue>> value,
        int count,
        NexusFormatter<TKey> keyFormatter,
        NexusFormatter<TValue> valueFormatter)
    {
        writer.WriteMapHeader(count);
        foreach (var pair in value)
        {
            keyFormatter.Serialize(ref writer, pair.Key);
            valueFormatter.Serialize(ref writer, pair.Value);
        }
    }

    public static Dictionary<TKey, TValue>? Deserialize<TKey, TValue>(
        ref MsgPackReader reader,
        NexusFormatter<TKey> keyFormatter,
        NexusFormatter<TValue> valueFormatter)
        where TKey : notnull
    {
        if (reader.TryReadNil())
            return null;

        var count = reader.ReadMapHeader();
        reader.Enter();
        var dictionary = new Dictionary<TKey, TValue>(count, CollectionComparers.Get<TKey>(reader.Options));
        for (var i = 0; i < count; i++)
        {
            TKey? key = default;
            TValue? item = default;
            keyFormatter.Deserialize(ref reader, ref key);
            valueFormatter.Deserialize(ref reader, ref item);
            if (key is null)
                throw new NexusSerializationException("Dictionary keys cannot be nil.");
            dictionary[key] = item!; // a nil value is a valid value for a nullable TValue
        }

        reader.Exit();
        return dictionary;
    }
}

public sealed class KeyValuePairFormatter<TKey, TValue> : NexusFormatter<KeyValuePair<TKey, TValue>>
{
    private NexusFormatter<TKey>? _key;
    private NexusFormatter<TValue>? _value;

    private NexusFormatter<TKey> KeyFormatter => _key ??= NexusFormatterRegistry.Get<TKey>();
    private NexusFormatter<TValue> ValueFormatter => _value ??= NexusFormatterRegistry.Get<TValue>();

    public override void Serialize(ref MsgPackWriter writer, KeyValuePair<TKey, TValue> value)
    {
        writer.WriteArrayHeader(2);
        KeyFormatter.Serialize(ref writer, value.Key);
        ValueFormatter.Serialize(ref writer, value.Value);
    }

    public override void Deserialize(ref MsgPackReader reader, ref KeyValuePair<TKey, TValue> value)
    {
        reader.ReadArrayHeader(2);
        reader.Enter();
        TKey key = default!;
        TValue item = default!;
        KeyFormatter.Deserialize(ref reader, ref key!);
        ValueFormatter.Deserialize(ref reader, ref item!);
        reader.Exit();
        value = new KeyValuePair<TKey, TValue>(key, item);
    }
}
