using System;

namespace NexNet.Serialization.Formatters;

#pragma warning disable CS1591 // Formatter members are self-describing.

/// <summary><c>ValueTuple</c> of 1 element(s) as array(1).</summary>
public sealed class ValueTupleFormatter<T1> : NexusFormatter<ValueTuple<T1>>
{
    private NexusFormatter<T1>? _f1;
    private NexusFormatter<T1> F1 => _f1 ??= NexusFormatterRegistry.Get<T1>();

    public override void Serialize(ref MsgPackWriter writer, ValueTuple<T1> value)
    {
        writer.WriteArrayHeader(1);
        F1.Serialize(ref writer, value.Item1);
    }

    public override void Deserialize(ref MsgPackReader reader, ref ValueTuple<T1> value)
    {
        reader.ReadArrayHeader(1);
        reader.Enter();
        T1 item1 = default!;
        F1.Deserialize(ref reader, ref item1!);
        reader.Exit();
        value = new ValueTuple<T1>(item1);
    }
}

/// <summary><c>ValueTuple</c> of 2 element(s) as array(2).</summary>
public sealed class ValueTupleFormatter<T1, T2> : NexusFormatter<ValueTuple<T1, T2>>
{
    private NexusFormatter<T1>? _f1;
    private NexusFormatter<T2>? _f2;
    private NexusFormatter<T1> F1 => _f1 ??= NexusFormatterRegistry.Get<T1>();
    private NexusFormatter<T2> F2 => _f2 ??= NexusFormatterRegistry.Get<T2>();

    public override void Serialize(ref MsgPackWriter writer, ValueTuple<T1, T2> value)
    {
        writer.WriteArrayHeader(2);
        F1.Serialize(ref writer, value.Item1);
        F2.Serialize(ref writer, value.Item2);
    }

    public override void Deserialize(ref MsgPackReader reader, ref ValueTuple<T1, T2> value)
    {
        reader.ReadArrayHeader(2);
        reader.Enter();
        T1 item1 = default!;
        F1.Deserialize(ref reader, ref item1!);
        T2 item2 = default!;
        F2.Deserialize(ref reader, ref item2!);
        reader.Exit();
        value = new ValueTuple<T1, T2>(item1, item2);
    }
}

/// <summary><c>ValueTuple</c> of 3 element(s) as array(3).</summary>
public sealed class ValueTupleFormatter<T1, T2, T3> : NexusFormatter<ValueTuple<T1, T2, T3>>
{
    private NexusFormatter<T1>? _f1;
    private NexusFormatter<T2>? _f2;
    private NexusFormatter<T3>? _f3;
    private NexusFormatter<T1> F1 => _f1 ??= NexusFormatterRegistry.Get<T1>();
    private NexusFormatter<T2> F2 => _f2 ??= NexusFormatterRegistry.Get<T2>();
    private NexusFormatter<T3> F3 => _f3 ??= NexusFormatterRegistry.Get<T3>();

    public override void Serialize(ref MsgPackWriter writer, ValueTuple<T1, T2, T3> value)
    {
        writer.WriteArrayHeader(3);
        F1.Serialize(ref writer, value.Item1);
        F2.Serialize(ref writer, value.Item2);
        F3.Serialize(ref writer, value.Item3);
    }

    public override void Deserialize(ref MsgPackReader reader, ref ValueTuple<T1, T2, T3> value)
    {
        reader.ReadArrayHeader(3);
        reader.Enter();
        T1 item1 = default!;
        F1.Deserialize(ref reader, ref item1!);
        T2 item2 = default!;
        F2.Deserialize(ref reader, ref item2!);
        T3 item3 = default!;
        F3.Deserialize(ref reader, ref item3!);
        reader.Exit();
        value = new ValueTuple<T1, T2, T3>(item1, item2, item3);
    }
}

/// <summary><c>ValueTuple</c> of 4 element(s) as array(4).</summary>
public sealed class ValueTupleFormatter<T1, T2, T3, T4> : NexusFormatter<ValueTuple<T1, T2, T3, T4>>
{
    private NexusFormatter<T1>? _f1;
    private NexusFormatter<T2>? _f2;
    private NexusFormatter<T3>? _f3;
    private NexusFormatter<T4>? _f4;
    private NexusFormatter<T1> F1 => _f1 ??= NexusFormatterRegistry.Get<T1>();
    private NexusFormatter<T2> F2 => _f2 ??= NexusFormatterRegistry.Get<T2>();
    private NexusFormatter<T3> F3 => _f3 ??= NexusFormatterRegistry.Get<T3>();
    private NexusFormatter<T4> F4 => _f4 ??= NexusFormatterRegistry.Get<T4>();

    public override void Serialize(ref MsgPackWriter writer, ValueTuple<T1, T2, T3, T4> value)
    {
        writer.WriteArrayHeader(4);
        F1.Serialize(ref writer, value.Item1);
        F2.Serialize(ref writer, value.Item2);
        F3.Serialize(ref writer, value.Item3);
        F4.Serialize(ref writer, value.Item4);
    }

    public override void Deserialize(ref MsgPackReader reader, ref ValueTuple<T1, T2, T3, T4> value)
    {
        reader.ReadArrayHeader(4);
        reader.Enter();
        T1 item1 = default!;
        F1.Deserialize(ref reader, ref item1!);
        T2 item2 = default!;
        F2.Deserialize(ref reader, ref item2!);
        T3 item3 = default!;
        F3.Deserialize(ref reader, ref item3!);
        T4 item4 = default!;
        F4.Deserialize(ref reader, ref item4!);
        reader.Exit();
        value = new ValueTuple<T1, T2, T3, T4>(item1, item2, item3, item4);
    }
}

/// <summary><c>ValueTuple</c> of 5 element(s) as array(5).</summary>
public sealed class ValueTupleFormatter<T1, T2, T3, T4, T5> : NexusFormatter<ValueTuple<T1, T2, T3, T4, T5>>
{
    private NexusFormatter<T1>? _f1;
    private NexusFormatter<T2>? _f2;
    private NexusFormatter<T3>? _f3;
    private NexusFormatter<T4>? _f4;
    private NexusFormatter<T5>? _f5;
    private NexusFormatter<T1> F1 => _f1 ??= NexusFormatterRegistry.Get<T1>();
    private NexusFormatter<T2> F2 => _f2 ??= NexusFormatterRegistry.Get<T2>();
    private NexusFormatter<T3> F3 => _f3 ??= NexusFormatterRegistry.Get<T3>();
    private NexusFormatter<T4> F4 => _f4 ??= NexusFormatterRegistry.Get<T4>();
    private NexusFormatter<T5> F5 => _f5 ??= NexusFormatterRegistry.Get<T5>();

    public override void Serialize(ref MsgPackWriter writer, ValueTuple<T1, T2, T3, T4, T5> value)
    {
        writer.WriteArrayHeader(5);
        F1.Serialize(ref writer, value.Item1);
        F2.Serialize(ref writer, value.Item2);
        F3.Serialize(ref writer, value.Item3);
        F4.Serialize(ref writer, value.Item4);
        F5.Serialize(ref writer, value.Item5);
    }

    public override void Deserialize(ref MsgPackReader reader, ref ValueTuple<T1, T2, T3, T4, T5> value)
    {
        reader.ReadArrayHeader(5);
        reader.Enter();
        T1 item1 = default!;
        F1.Deserialize(ref reader, ref item1!);
        T2 item2 = default!;
        F2.Deserialize(ref reader, ref item2!);
        T3 item3 = default!;
        F3.Deserialize(ref reader, ref item3!);
        T4 item4 = default!;
        F4.Deserialize(ref reader, ref item4!);
        T5 item5 = default!;
        F5.Deserialize(ref reader, ref item5!);
        reader.Exit();
        value = new ValueTuple<T1, T2, T3, T4, T5>(item1, item2, item3, item4, item5);
    }
}

/// <summary><c>ValueTuple</c> of 6 element(s) as array(6).</summary>
public sealed class ValueTupleFormatter<T1, T2, T3, T4, T5, T6> : NexusFormatter<ValueTuple<T1, T2, T3, T4, T5, T6>>
{
    private NexusFormatter<T1>? _f1;
    private NexusFormatter<T2>? _f2;
    private NexusFormatter<T3>? _f3;
    private NexusFormatter<T4>? _f4;
    private NexusFormatter<T5>? _f5;
    private NexusFormatter<T6>? _f6;
    private NexusFormatter<T1> F1 => _f1 ??= NexusFormatterRegistry.Get<T1>();
    private NexusFormatter<T2> F2 => _f2 ??= NexusFormatterRegistry.Get<T2>();
    private NexusFormatter<T3> F3 => _f3 ??= NexusFormatterRegistry.Get<T3>();
    private NexusFormatter<T4> F4 => _f4 ??= NexusFormatterRegistry.Get<T4>();
    private NexusFormatter<T5> F5 => _f5 ??= NexusFormatterRegistry.Get<T5>();
    private NexusFormatter<T6> F6 => _f6 ??= NexusFormatterRegistry.Get<T6>();

    public override void Serialize(ref MsgPackWriter writer, ValueTuple<T1, T2, T3, T4, T5, T6> value)
    {
        writer.WriteArrayHeader(6);
        F1.Serialize(ref writer, value.Item1);
        F2.Serialize(ref writer, value.Item2);
        F3.Serialize(ref writer, value.Item3);
        F4.Serialize(ref writer, value.Item4);
        F5.Serialize(ref writer, value.Item5);
        F6.Serialize(ref writer, value.Item6);
    }

    public override void Deserialize(ref MsgPackReader reader, ref ValueTuple<T1, T2, T3, T4, T5, T6> value)
    {
        reader.ReadArrayHeader(6);
        reader.Enter();
        T1 item1 = default!;
        F1.Deserialize(ref reader, ref item1!);
        T2 item2 = default!;
        F2.Deserialize(ref reader, ref item2!);
        T3 item3 = default!;
        F3.Deserialize(ref reader, ref item3!);
        T4 item4 = default!;
        F4.Deserialize(ref reader, ref item4!);
        T5 item5 = default!;
        F5.Deserialize(ref reader, ref item5!);
        T6 item6 = default!;
        F6.Deserialize(ref reader, ref item6!);
        reader.Exit();
        value = new ValueTuple<T1, T2, T3, T4, T5, T6>(item1, item2, item3, item4, item5, item6);
    }
}

/// <summary><c>ValueTuple</c> of 7 element(s) as array(7).</summary>
public sealed class ValueTupleFormatter<T1, T2, T3, T4, T5, T6, T7> : NexusFormatter<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>
{
    private NexusFormatter<T1>? _f1;
    private NexusFormatter<T2>? _f2;
    private NexusFormatter<T3>? _f3;
    private NexusFormatter<T4>? _f4;
    private NexusFormatter<T5>? _f5;
    private NexusFormatter<T6>? _f6;
    private NexusFormatter<T7>? _f7;
    private NexusFormatter<T1> F1 => _f1 ??= NexusFormatterRegistry.Get<T1>();
    private NexusFormatter<T2> F2 => _f2 ??= NexusFormatterRegistry.Get<T2>();
    private NexusFormatter<T3> F3 => _f3 ??= NexusFormatterRegistry.Get<T3>();
    private NexusFormatter<T4> F4 => _f4 ??= NexusFormatterRegistry.Get<T4>();
    private NexusFormatter<T5> F5 => _f5 ??= NexusFormatterRegistry.Get<T5>();
    private NexusFormatter<T6> F6 => _f6 ??= NexusFormatterRegistry.Get<T6>();
    private NexusFormatter<T7> F7 => _f7 ??= NexusFormatterRegistry.Get<T7>();

    public override void Serialize(ref MsgPackWriter writer, ValueTuple<T1, T2, T3, T4, T5, T6, T7> value)
    {
        writer.WriteArrayHeader(7);
        F1.Serialize(ref writer, value.Item1);
        F2.Serialize(ref writer, value.Item2);
        F3.Serialize(ref writer, value.Item3);
        F4.Serialize(ref writer, value.Item4);
        F5.Serialize(ref writer, value.Item5);
        F6.Serialize(ref writer, value.Item6);
        F7.Serialize(ref writer, value.Item7);
    }

    public override void Deserialize(ref MsgPackReader reader, ref ValueTuple<T1, T2, T3, T4, T5, T6, T7> value)
    {
        reader.ReadArrayHeader(7);
        reader.Enter();
        T1 item1 = default!;
        F1.Deserialize(ref reader, ref item1!);
        T2 item2 = default!;
        F2.Deserialize(ref reader, ref item2!);
        T3 item3 = default!;
        F3.Deserialize(ref reader, ref item3!);
        T4 item4 = default!;
        F4.Deserialize(ref reader, ref item4!);
        T5 item5 = default!;
        F5.Deserialize(ref reader, ref item5!);
        T6 item6 = default!;
        F6.Deserialize(ref reader, ref item6!);
        T7 item7 = default!;
        F7.Deserialize(ref reader, ref item7!);
        reader.Exit();
        value = new ValueTuple<T1, T2, T3, T4, T5, T6, T7>(item1, item2, item3, item4, item5, item6, item7);
    }
}
