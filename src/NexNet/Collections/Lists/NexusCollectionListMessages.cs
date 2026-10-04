using System;
using System.Runtime.CompilerServices;
using NexNet.Internals;
using NexNet.Serialization;

namespace NexNet.Collections.Lists;

internal class NexusUnionAttribute : Attribute
{
}

/// <summary>
/// Union of list synchronization messages. Written as <c>[tag, [flags, ...fields]]</c> by
/// <see cref="NexusCollectionListMessageFormatter"/>. Tags:
/// 0 ResetStart, 1 ResetValues, 2 ResetComplete, 3 Clear, 4 Insert, 5 Replace, 6 Move, 7 Remove, 8 Noop.
/// </summary>
[NexusUnion]
internal partial interface INexusCollectionListMessage : INexusCollectionUnion<INexusCollectionListMessage>
{
    void SerializeBody(ref MsgPackWriter writer);

    void DeserializeBody(ref MsgPackReader reader);
}

/// <summary>
/// Hand-written union formatter for <see cref="INexusCollectionListMessage"/>. Deserialized messages are rented
/// from the per-type message caches.
/// </summary>
internal sealed class NexusCollectionListMessageFormatter : NexusFormatter<INexusCollectionListMessage>
{
    public static readonly NexusCollectionListMessageFormatter Instance = new();

#pragma warning disable CA2255 // Registers the internal protocol union formatter.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => NexusFormatterRegistry.Register(Instance);

    public override void Serialize(ref MsgPackWriter writer, INexusCollectionListMessage? value)
    {
        ushort tag = value switch
        {
            null => ushort.MaxValue,
            NexusCollectionListResetStartMessage => 0,
            NexusCollectionListResetValuesMessage => 1,
            NexusCollectionListResetCompleteMessage => 2,
            NexusCollectionListClearMessage => 3,
            NexusCollectionListInsertMessage => 4,
            NexusCollectionListReplaceMessage => 5,
            NexusCollectionListMoveMessage => 6,
            NexusCollectionListRemoveMessage => 7,
            NexusCollectionListNoopMessage => 8,
            _ => throw NexusSerializationException.UnknownUnionType(value.GetType(), typeof(INexusCollectionListMessage))
        };

        if (value == null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteArrayHeader(2);
        writer.Write(tag);
        value.SerializeBody(ref writer);
    }

    public override void Deserialize(ref MsgPackReader reader, ref INexusCollectionListMessage? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        reader.ReadArrayHeader(2);
        var tag = reader.ReadUInt16();
        INexusCollectionListMessage message = tag switch
        {
            0 => NexusCollectionListResetStartMessage.Rent(),
            1 => NexusCollectionListResetValuesMessage.Rent(),
            2 => NexusCollectionListResetCompleteMessage.Rent(),
            3 => NexusCollectionListClearMessage.Rent(),
            4 => NexusCollectionListInsertMessage.Rent(),
            5 => NexusCollectionListReplaceMessage.Rent(),
            6 => NexusCollectionListMoveMessage.Rent(),
            7 => NexusCollectionListRemoveMessage.Rent(),
            8 => NexusCollectionListNoopMessage.Rent(),
            _ => throw NexusSerializationException.UnknownUnionTag(tag, typeof(INexusCollectionListMessage))
        };

        reader.Enter();
        message.DeserializeBody(ref reader);
        reader.Exit();
        value = message;
    }
}

internal partial class NexusCollectionListResetStartMessage
    : NexusCollectionMessage<NexusCollectionListResetStartMessage, INexusCollectionListMessage>, INexusCollectionListMessage
{
    public int Version { get; set; }

    public int TotalValues { get; set; }

    public override INexusCollectionListMessage Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.TotalValues = TotalValues;
        return clone;
    }

    public override void SerializeBody(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(3);
        writer.Write((byte)Flags);
        writer.Write(Version);
        writer.Write(TotalValues);
    }

    public override void DeserializeBody(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(3);
        Flags = (NexusCollectionMessageFlags)reader.ReadByte();
        Version = reader.ReadInt32();
        TotalValues = reader.ReadInt32();
    }
}

internal partial class NexusCollectionListResetCompleteMessage :
    NexusCollectionMessage<NexusCollectionListResetCompleteMessage, INexusCollectionListMessage>, INexusCollectionListMessage
{
    public override INexusCollectionListMessage Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;
        return clone;
    }

    public override void SerializeBody(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(1);
        writer.Write((byte)Flags);
    }

    public override void DeserializeBody(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(1);
        Flags = (NexusCollectionMessageFlags)reader.ReadByte();
    }
}

internal partial class NexusCollectionListResetValuesMessage
    : NexusCollectionValueMessage<NexusCollectionListResetValuesMessage, INexusCollectionListMessage>, INexusCollectionListMessage
{
    public Memory<byte> Values
    {
        get => base.ValueCore;
        set => base.ValueCore = value;
    }

    public override INexusCollectionListMessage Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;

        // Reference the values only as we don't need a deep copy of the values.
        clone.Values = Values;
        return clone;
    }

    public override void SerializeBody(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(2);
        writer.Write((byte)Flags);
        PayloadSerializer.WriteEmbedded(ref writer, Values.Span);
    }

    public override void DeserializeBody(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(2);
        Flags = (NexusCollectionMessageFlags)reader.ReadByte();
        ReadValueCore(ref reader);
    }
}

internal partial class NexusCollectionListInsertMessage
    : NexusCollectionValueMessage<NexusCollectionListInsertMessage, INexusCollectionListMessage>, INexusCollectionListMessage
{
    public int Version { get; set; }

    public int Index { get; set; }

    public Memory<byte> Value
    {
        get => base.ValueCore;
        set => base.ValueCore = value;
    }

    public override INexusCollectionListMessage Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.Index = Index;
        clone.Value = Value;
        return clone;
    }

    public override void SerializeBody(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(4);
        writer.Write((byte)Flags);
        writer.Write(Version);
        writer.Write(Index);
        PayloadSerializer.WriteEmbedded(ref writer, Value.Span);
    }

    public override void DeserializeBody(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(4);
        Flags = (NexusCollectionMessageFlags)reader.ReadByte();
        Version = reader.ReadInt32();
        Index = reader.ReadInt32();
        ReadValueCore(ref reader);
    }
}

internal partial class NexusCollectionListReplaceMessage
    : NexusCollectionValueMessage<NexusCollectionListReplaceMessage, INexusCollectionListMessage>, INexusCollectionListMessage
{
    public int Version { get; set; }

    public int Index { get; set; }

    public Memory<byte> Value
    {
        get => base.ValueCore;
        set => base.ValueCore = value;
    }

    public override INexusCollectionListMessage Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.Index = Index;
        clone.Value = Value;
        return clone;
    }

    public override void SerializeBody(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(4);
        writer.Write((byte)Flags);
        writer.Write(Version);
        writer.Write(Index);
        PayloadSerializer.WriteEmbedded(ref writer, Value.Span);
    }

    public override void DeserializeBody(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(4);
        Flags = (NexusCollectionMessageFlags)reader.ReadByte();
        Version = reader.ReadInt32();
        Index = reader.ReadInt32();
        ReadValueCore(ref reader);
    }
}

internal partial class NexusCollectionListMoveMessage
    : NexusCollectionMessage<NexusCollectionListMoveMessage, INexusCollectionListMessage>, INexusCollectionListMessage
{
    public int Version { get; set; }

    public int FromIndex { get; set; }

    public int ToIndex { get; set; }

    public override INexusCollectionListMessage Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.FromIndex = FromIndex;
        clone.ToIndex = ToIndex;
        return clone;
    }

    public override void SerializeBody(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(4);
        writer.Write((byte)Flags);
        writer.Write(Version);
        writer.Write(FromIndex);
        writer.Write(ToIndex);
    }

    public override void DeserializeBody(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(4);
        Flags = (NexusCollectionMessageFlags)reader.ReadByte();
        Version = reader.ReadInt32();
        FromIndex = reader.ReadInt32();
        ToIndex = reader.ReadInt32();
    }
}

internal partial class NexusCollectionListClearMessage :
    NexusCollectionMessage<NexusCollectionListClearMessage, INexusCollectionListMessage>, INexusCollectionListMessage
{
    public int Version { get; set; }

    public override INexusCollectionListMessage Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        return clone;
    }

    public override void SerializeBody(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(2);
        writer.Write((byte)Flags);
        writer.Write(Version);
    }

    public override void DeserializeBody(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(2);
        Flags = (NexusCollectionMessageFlags)reader.ReadByte();
        Version = reader.ReadInt32();
    }
}

internal partial class NexusCollectionListRemoveMessage :
    NexusCollectionMessage<NexusCollectionListRemoveMessage, INexusCollectionListMessage>, INexusCollectionListMessage
{
    public int Version { get; set; }

    public int Index { get; set; }

    public override INexusCollectionListMessage Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.Index = Index;
        return clone;
    }

    public override void SerializeBody(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(3);
        writer.Write((byte)Flags);
        writer.Write(Version);
        writer.Write(Index);
    }

    public override void DeserializeBody(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(3);
        Flags = (NexusCollectionMessageFlags)reader.ReadByte();
        Version = reader.ReadInt32();
        Index = reader.ReadInt32();
    }
}

internal partial class NexusCollectionListNoopMessage :
    NexusCollectionMessage<NexusCollectionListNoopMessage, INexusCollectionListMessage>, INexusCollectionListMessage
{
    public override INexusCollectionListMessage Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;
        return clone;
    }

    public override void SerializeBody(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(1);
        writer.Write((byte)Flags);
    }

    public override void DeserializeBody(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(1);
        Flags = (NexusCollectionMessageFlags)reader.ReadByte();
    }
}
