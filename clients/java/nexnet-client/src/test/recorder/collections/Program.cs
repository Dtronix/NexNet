// Records collection-items.txt for the Java collection tests: collection pipe items (protocol specification
// sections 4.11 and 12.3) serialized by the C# union formatter, NexusCollectionListMessageFormatter, with values
// serialized by PayloadSerializer as the C# client and server do. One line per item: "name hex".
// The assembly is named NexNet.Fuzz to reach the internal message classes (InternalsVisibleTo).
// Regenerate (from the repository root):
//   dotnet run --project clients/java/nexnet-client/src/test/recorder/collections -c Release -- clients/java/nexnet-client/src/test/resources/com/dtronix/nexnet/collections/collection-items.txt
using System.Buffers;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Serialization;

namespace CollectionRecorder;

public static class Program
{
    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    static string Item(INexusCollectionListMessage m)
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buf);
        NexusCollectionListMessageFormatter.Instance.Serialize(ref w, m);
        w.Flush();
        return Hex(buf.WrittenSpan);
    }

    static NexusCollectionMessageFlags F(bool ack) => ack ? NexusCollectionMessageFlags.Ack : NexusCollectionMessageFlags.Unset;

    static INexusCollectionListMessage Start(bool ack, int version, int total)
    {
        var m = NexusCollectionListResetStartMessage.Rent();
        m.Flags = F(ack);
        m.Version = version;
        m.TotalValues = total;
        return m;
    }

    static INexusCollectionListMessage Values<T>(T[] values)
    {
        var m = NexusCollectionListResetValuesMessage.Rent();
        m.Values = PayloadSerializer.Serialize(values);
        return m;
    }

    static INexusCollectionListMessage Complete(bool ack)
    {
        var m = NexusCollectionListResetCompleteMessage.Rent();
        m.Flags = F(ack);
        return m;
    }

    static INexusCollectionListMessage Clear(bool ack, int version)
    {
        var m = NexusCollectionListClearMessage.Rent();
        m.Flags = F(ack);
        m.Version = version;
        return m;
    }

    static INexusCollectionListMessage Insert<T>(bool ack, int version, int index, T value)
    {
        var m = NexusCollectionListInsertMessage.Rent();
        m.Flags = F(ack);
        m.Version = version;
        m.Index = index;
        m.Value = PayloadSerializer.Serialize(value);
        return m;
    }

    static INexusCollectionListMessage Replace<T>(bool ack, int version, int index, T value)
    {
        var m = NexusCollectionListReplaceMessage.Rent();
        m.Flags = F(ack);
        m.Version = version;
        m.Index = index;
        m.Value = PayloadSerializer.Serialize(value);
        return m;
    }

    static INexusCollectionListMessage Move(bool ack, int version, int from, int to)
    {
        var m = NexusCollectionListMoveMessage.Rent();
        m.Flags = F(ack);
        m.Version = version;
        m.FromIndex = from;
        m.ToIndex = to;
        return m;
    }

    static INexusCollectionListMessage Remove(bool ack, int version, int index)
    {
        var m = NexusCollectionListRemoveMessage.Rent();
        m.Flags = F(ack);
        m.Version = version;
        m.Index = index;
        return m;
    }

    static INexusCollectionListMessage Noop(bool ack)
    {
        var m = NexusCollectionListNoopMessage.Rent();
        m.Flags = F(ack);
        return m;
    }

    public static void Main(string[] args)
    {
        var items = new (string Name, string Comment, INexusCollectionListMessage Message)[]
        {
            ("reset-start", "flags 0, version 7, totalValues 3", Start(false, 7, 3)),
            ("reset-start-empty", "flags 0, version 0, totalValues 0", Start(false, 0, 0)),
            ("reset-start-large", "flags Ack, version 70000, totalValues 300", Start(true, 70000, 300)),
            ("reset-start-negative", "flags 0, version 1, totalValues -1", Start(false, 1, -1)),
            ("reset-values-int", "int[] {1, 2, -5} (ext 78 kind 4)", Values(new[] { 1, 2, -5 })),
            ("reset-values-int-empty", "int[] {} (ext 78 kind 4)", Values(Array.Empty<int>())),
            ("reset-values-int-40", "int[] 0..39 (one full C# batch)", Values(Enumerable.Range(0, 40).ToArray())),
            ("reset-values-string", "string?[] {a, hello, null}", Values(new string?[] { "a", "hello", null })),
            ("reset-values-byte", "byte[] {0, 255, 7} (bin)", Values(new byte[] { 0, 255, 7 })),
            ("reset-values-sbyte", "sbyte[] {-128, 127} (kind 1)", Values(new sbyte[] { -128, 127 })),
            ("reset-values-short", "short[] {-32768, 300} (kind 2)", Values(new short[] { -32768, 300 })),
            ("reset-values-ushort", "ushort[] {65535, 1} (kind 3)", Values(new ushort[] { 65535, 1 })),
            ("reset-values-uint", "uint[] {4294967295, 0} (kind 5)", Values(new uint[] { uint.MaxValue, 0 })),
            ("reset-values-long", "long[] {long.MinValue, long.MaxValue, 0} (kind 6)", Values(new[] { long.MinValue, long.MaxValue, 0L })),
            ("reset-values-ulong", "ulong[] {ulong.MaxValue, 5} (kind 7)", Values(new[] { ulong.MaxValue, 5UL })),
            ("reset-values-float", "float[] {1.5, -0.25} (kind 8)", Values(new[] { 1.5f, -0.25f })),
            ("reset-values-double", "double[] {3.25, -1e300} (kind 9)", Values(new[] { 3.25, -1e300 })),
            ("reset-values-char", "char[] {A, e-acute 0xE9} (kind 10)", Values(new[] { 'A', (char)0xE9 })),
            ("reset-values-half", "Half[] {1.5, -2, min normal 0x0400, min subnormal 0x0001} (kind 11)",
                Values(new[] { (Half)1.5f, (Half)(-2f), BitConverter.UInt16BitsToHalf(0x0400), BitConverter.UInt16BitsToHalf(0x0001) })),
            ("reset-values-bool", "bool[] {true, false} (array)", Values(new[] { true, false })),
            ("reset-complete", "flags 0", Complete(false)),
            ("clear", "flags 0, version 5", Clear(false, 5)),
            ("clear-ack", "flags Ack, version 6", Clear(true, 6)),
            ("insert-int", "flags 0, version 8, index 2, value 42", Insert(false, 8, 2, 42)),
            ("insert-int-ack", "flags Ack, version 9, index 0, value -1", Insert(true, 9, 0, -1)),
            ("insert-int-large", "flags 0, version 1000000, index 65536, value int.MinValue", Insert(false, 1000000, 65536, int.MinValue)),
            ("insert-string", "flags 0, version 0, index 0, value x", Insert(false, 0, 0, "x")),
            ("replace-int", "flags 0, version 11, index 1, value 99", Replace(false, 11, 1, 99)),
            ("replace-string-ack", "flags Ack, version 12, index 0, value y", Replace(true, 12, 0, "y")),
            ("move", "flags 0, version 13, fromIndex 3, toIndex 0", Move(false, 13, 3, 0)),
            ("move-ack", "flags Ack, version 14, fromIndex 0, toIndex 2", Move(true, 14, 0, 2)),
            ("remove", "flags 0, version 15, index 1", Remove(false, 15, 1)),
            ("remove-ack", "flags Ack, version 16, index 0", Remove(true, 16, 0)),
            ("noop", "flags 0", Noop(false)),
            ("noop-ack", "flags Ack", Noop(true)),
            // Client requests: every request has flags 0 and carries the base version.
            ("request-insert-int", "client AddAsync(42) at version 4 with 2 items", Insert(false, 4, 2, 42)),
            ("request-insert-string", "client InsertAsync(1, hello) at version 300", Insert(false, 300, 1, "hello")),
            ("request-replace-int", "client ReplaceAsync(0, -7) at version 5", Replace(false, 5, 0, -7)),
            ("request-move", "client MoveAsync(2, 0) at version 6", Move(false, 6, 2, 0)),
            ("request-remove", "client RemoveAtAsync(1) at version 70000", Remove(false, 70000, 1)),
            ("request-clear", "client ClearAsync() at version 0", Clear(false, 0)),
        };

        var lines = new List<string>
        {
            "# Collection pipe items produced by the C# runtime (NexusCollectionListMessageFormatter; values by PayloadSerializer).",
            "# Generated by clients/java/nexnet-client/src/test/recorder/collections; do not edit.",
        };
        foreach (var (name, comment, message) in items)
        {
            lines.Add("# " + name + ": " + comment);
            lines.Add(name + " " + Item(message));
        }

        var path = args[0];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, string.Join('\n', lines) + '\n');
        Console.WriteLine($"Wrote {items.Length} items to {path}");
    }
}
