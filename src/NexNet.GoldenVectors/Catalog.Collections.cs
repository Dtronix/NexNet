using NexNet.Serialization;

namespace NexNet.GoldenVectors;

public static partial class GoldenGenerator
{
    /// <summary>The union of the collection-sync items (protocol specification §4.11, §12).</summary>
    public const string CollectionListMessage = "CollectionListMessage";

    /// <summary>
    /// The bodies of the collection-sync items (protocol specification §4.11): each an exact-length array whose first
    /// element is the flags (bit 0 Ack). The embedded values are one MessagePack value of the list's item type
    /// (`Insert`, `Replace`) or of a chunk of items (`ResetValues`, a `Memory<T>`), carried as their bytes.
    /// </summary>
    private static IEnumerable<MessageSchema> CollectionMessages()
    {
        yield return new MessageSchema("CollectionListResetStart", null, 3, 3, [("flags", "byte"), ("version", "int"), ("totalValues", "int")]);
        yield return new MessageSchema("CollectionListResetValues", null, 2, 2, [("flags", "byte"), ("values", TypeExpr.Raw)]);
        yield return new MessageSchema("CollectionListResetComplete", null, 1, 1, [("flags", "byte")]);
        yield return new MessageSchema("CollectionListClear", null, 2, 2, [("flags", "byte"), ("version", "int")]);
        yield return new MessageSchema("CollectionListInsert", null, 4, 4, [("flags", "byte"), ("version", "int"), ("index", "int"), ("value", TypeExpr.Raw)]);
        yield return new MessageSchema("CollectionListReplace", null, 4, 4, [("flags", "byte"), ("version", "int"), ("index", "int"), ("value", TypeExpr.Raw)]);
        yield return new MessageSchema("CollectionListMove", null, 4, 4, [("flags", "byte"), ("version", "int"), ("fromIndex", "int"), ("toIndex", "int")]);
        yield return new MessageSchema("CollectionListRemove", null, 3, 3, [("flags", "byte"), ("version", "int"), ("index", "int")]);
        yield return new MessageSchema("CollectionListNoop", null, 1, 1, [("flags", "byte")]);
    }

    /// <summary>The collection-sync item union: `[tag, body]`, the cases the bodies of <see cref="CollectionMessages"/>.</summary>
    private static IEnumerable<(string Id, IReadOnlyList<(ushort Tag, string CaseId)> Cases)> CollectionUnions()
    {
        yield return (CollectionListMessage,
        [
            (0, "CollectionListResetStart"), (1, "CollectionListResetValues"), (2, "CollectionListResetComplete"),
            (3, "CollectionListClear"), (4, "CollectionListInsert"), (5, "CollectionListReplace"), (6, "CollectionListMove"),
            (7, "CollectionListRemove"), (8, "CollectionListNoop"),
        ]);
    }

    private static void CollectionVectors(GoldenBuilder b)
    {
        const string t = CollectionListMessage;
        b.File("collections");

        // The embedded values, as the C# list serializes them (an INexusList<int> and an INexusList<string?>).
        var ints = Raw(new Memory<int>([1, 2, 3]));
        var noInts = Raw(Memory<int>.Empty);
        var strings = Raw(new[] { "a", "b" });
        var five = Raw(5);
        var x = Raw("x");
        var y = Raw("y");

        // Reset sequence (§12.4): ResetStart, ResetValues chunks, ResetComplete. The server sends them without Ack.
        b.BothJson(t, "collection.reset-start.empty", "[0,[0,0,0]]", "an empty list at version 0");
        b.BothJson(t, "collection.reset-start", "[0,[0,42,3]]", "a snapshot of 3 values at version 42");
        b.BothJson(t, "collection.reset-start.max", "[0,[0,2147483647,100000]]", "int32 version and count");
        b.BothJson(t, "collection.reset-values.int", $"""[1,[0,"{ints}"]]""", "a chunk of an INexusList<int>: Memory<int> [1, 2, 3], ext 78");
        b.BothJson(t, "collection.reset-values.int.empty", $"""[1,[0,"{noInts}"]]""", "an empty Memory<int> chunk");
        b.BothJson(t, "collection.reset-values.string", $"""[1,[0,"{strings}"]]""", "a chunk of an INexusList<string>: [\"a\", \"b\"], an array");
        b.BothJson(t, "collection.reset-complete", "[2,[0]]");

        // Changes (§12.3). Server -> client: version is the list version after the change, Ack on the originator's
        // copy only. Client -> server: flags 0, version the base version. Version 0 is unversioned (relay).
        b.BothJson(t, "collection.clear", "[3,[0,0]]", "a request on an empty list, or a relay change");
        b.BothJson(t, "collection.clear.ack", "[3,[1,7]]", "Ack: the originator's copy");
        b.BothJson(t, "collection.insert", $"""[4,[0,0,0,"{five}"]]""", "insert 5 at 0, version 0");
        b.BothJson(t, "collection.insert.versioned", $"""[4,[0,70000,2,"{x}"]]""", "insert \"x\" at 2; version 70000 is a uint32 on the wire");
        b.BothJson(t, "collection.insert.ack", $"""[4,[1,12,2,"{x}"]]""");
        b.BothJson(t, "collection.insert.nil-value", "[4,[0,3,0,null]]", "a nil item value (INexusList<string?>): the embedded value is nil, null as everywhere");
        b.BothJson(t, "collection.replace", $"""[5,[0,3,1,"{y}"]]""", "replace the item at 1 with \"y\"");
        b.BothJson(t, "collection.replace.ack", $"""[5,[1,4,1,"{y}"]]""");
        b.BothJson(t, "collection.move", "[6,[0,4,0,2]]", "the item at 0 ends up at 2");
        b.BothJson(t, "collection.move.ack", "[6,[1,5,2,0]]");
        b.BothJson(t, "collection.remove", "[7,[0,0,1]]");
        b.BothJson(t, "collection.remove.ack", "[7,[1,9,0]]");
        b.BothJson(t, "collection.noop.ack", "[8,[1]]", "the server's \"not applied\" answer: to the originator only, always Ack");
        b.BothJson(t, "collection.noop", "[8,[0]]", "Noop without Ack decodes too");

        // Non-minimal integer forms are accepted.
        b.DecodeJson(t, "collection.clear.from-int32", "92039200d200000005", "[3,[0,5]]", "version as int32 (d2)");
        b.DecodeJson(t, "collection.noop.from-uint8", "920891cc01", "[8,[1]]", "flags as uint8 (cc)");
        b.DecodeJson(t, "collection.noop.from-uint16-tag", "92cd00089101", "[8,[1]]", "tag as uint16 (cd)");

        // Rejects (in reject.jsonl).
        b.RejectJson(t, "collection.reject.unknown-tag", "92099100", "value", "tag 9 is not a collection item");
        b.RejectJson(t, "collection.reject.tag-65536", "92ce000100009100", "range", "tags are uint16");
        b.RejectJson(t, "collection.reject.union-count-3", "93089101c0", "count", "an item is [tag, body]");
        b.RejectJson(t, "collection.reject.nil-body", "9208c0", "type");
        b.RejectJson(t, "collection.reject.noop-count-2", "9208920100", "count", "bodies have their exact element count");
        b.RejectJson(t, "collection.reject.reset-start-count-2", "920092002a", "count");
        b.RejectJson(t, "collection.reject.insert-count-3", "920493000000", "count", "Insert without its value");
        b.RejectJson(t, "collection.reject.flags-256", "920891cd0100", "range", "flags are uint8");
        b.RejectJson(t, "collection.reject.flags-negative", "920891ff", "range");
        b.RejectJson(t, "collection.reject.version-2147483648", "92039200ce80000000", "range", "versions are int32");
        b.RejectJson(t, "collection.reject.insert-truncated", "920494000000", "truncated", "the value is missing");
        b.RejectJson(t, "collection.reject.trailing", "92089101c0", "trailing");
    }

    private static string Raw<T>(T value) => Convert.ToBase64String(NexusSerializer.Serialize(value));
}
