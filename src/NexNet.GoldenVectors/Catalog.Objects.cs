using System.Numerics;
using NexNet.TestNexus.Interop;

namespace NexNet.GoldenVectors;

public static partial class GoldenGenerator
{
    private static InteropAllTypes AllTypes() => new()
    {
        BoolValue = true,
        SByteValue = -100,
        ByteValue = 200,
        Int16Value = -30000,
        UInt16Value = 60000,
        Int32Value = -2_000_000_000,
        UInt32Value = 4_000_000_000,
        Int64Value = long.MinValue,
        UInt64Value = ulong.MaxValue,
        SingleValue = 1.5f,
        DoubleValue = -0.1,
        HalfValue = (Half)0.5,
        CharValue = 'Z',
        DecimalValue = -1.5m,
        StringValue = "zoo",
        BytesValue = [0, 255],
        DateTimeValue = new DateTime(637134336000000000, DateTimeKind.Utc),
        DateTimeOffsetValue = new DateTimeOffset(637134336000000000 + 330 * TimeSpan.TicksPerMinute, TimeSpan.FromMinutes(330)),
        TimeSpanValue = TimeSpan.FromMilliseconds(-1500),
        DateOnlyValue = new DateOnly(2020, 1, 1),
        TimeOnlyValue = new TimeOnly(23, 59, 59),
        GuidValue = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        BigIntegerValue = BigInteger.Pow(2, 70),
        UriValue = new Uri("https://example.com/"),
        VersionValue = new Version(1, 2, 3),
        EnumValue = InteropEnum.Negative,
        FlagsValue = InteropFlags.A | InteropFlags.High,
        LongEnumValue = InteropLongEnum.BeyondDouble,
        NullableInt32Value = 42,
        Int32ArrayValue = [1, 2, 3],
        StringListValue = ["x", "y"],
        StringMapValue = new Dictionary<string, int> { ["k"] = 1 },
    };

    private static void Objects(GoldenBuilder b)
    {
        b.File("objects");

        // ----- objects: arrays by key, nil in the gaps (§10.2)
        b.Both("InteropAllTypes", "object.all-types.defaults", new InteropAllTypes(), "every member at its default");
        b.Both("InteropAllTypes", "object.all-types", AllTypes(), "one member of every built-in type");
        b.Both<InteropAllTypes?>("InteropAllTypes", "object.all-types.nil", null);
        b.Both("InteropStruct", "object.struct", new InteropStruct { X = -3, Y = 2.5, Label = "p" });
        b.Both("InteropStruct?", "object.struct.nullable", (InteropStruct?)new InteropStruct { X = 1 });
        b.Both<InteropStruct?>("InteropStruct?", "object.struct.nullable-nil", null);
        b.Both("InteropRecord", "object.record", new InteropRecord("rec", 2, new InteropStruct { X = 1, Y = -1, Label = null }));
        b.Both("InteropRecordStruct", "object.record-struct", new InteropRecordStruct(long.MaxValue, Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301")));
        b.Both("InteropSparse", "object.sparse", new InteropSparse { First = 1, Fourth = "four" }, "keys 1 and 2 are unused: nil");
        b.Both("InteropItem", "object.item", new InteropItem(7, "seven"));
        b.Both("InteropNested", "object.nested", new InteropNested
        {
            Record = new InteropRecord("r", 1, new InteropStruct { X = 2 }),
            Items = [new InteropItem(1, "a")],
            ByIndex = new Dictionary<int, InteropStruct> { [0] = new() { X = 5, Label = "five" } },
            Shape = new InteropCircle { Radius = 1 },
            Child = new InteropNested { Items = [] },
        });
        b.Both("InteropBox<string>", "object.box-string", new InteropBox<string> { Value = "boxed" }, "a generic object");
        b.Both("InteropBox<InteropItem>", "object.box-item", new InteropBox<InteropItem> { Value = new InteropItem(3, "three") });
        b.Both("InteropPair<int,InteropItem>", "object.pair", new InteropPair<int, InteropItem> { First = 9, Second = [new InteropItem(1, "x"), new InteropItem(2, "y")] },
            "List<TSecond> with TSecond bound to an object is an element-wise array");

        // Peers of other versions: a shorter array leaves the missing members at their defaults, a longer one is skipped
        // past the highest known key, and a value in an unused key is skipped.
        b.Decode<InteropItem>("InteropItem", "object.item.older-peer", "9105", "Name is missing: null");
        b.Decode<InteropItem>("InteropItem", "object.item.newer-peer", "9305a161c3", "an unknown third member is skipped");
        b.Decode<InteropSparse>("InteropSparse", "object.sparse.gap-values", "9401c3c400a178", "values at the unused keys are skipped");
        b.Decode<InteropStruct>("InteropStruct", "object.struct.empty-array", "90", "every member at its default");
        b.Reject<InteropItem>("InteropItem", "object.reject.map", "81a16101", "type");
        b.Reject<InteropItem>("InteropItem", "object.reject.member-type", "92a161a161", "type");
        b.Reject<InteropStruct>("InteropStruct", "object.reject.struct-nil", "c0", "type", "a struct is never nil");

        // Depth (§11): every array or map of the value is one level, objects included.
        var nested = new InteropNested { Child = new InteropNested { Child = new InteropNested() } };
        b.Both("InteropNested", "limit.depth.at-limit", nested, "3 nested objects with maxDepth 3", new GoldenOptions(MaxDepth: 3));
        b.Reject<InteropNested>("InteropNested", "limit.reject.depth", Repeat("95c0c0c0c0", 4) + "c0", "depth", "4 nested objects with maxDepth 3", new GoldenOptions(MaxDepth: 3));
        b.Reject<InteropNested>("InteropNested", "limit.reject.depth-default", Repeat("95c0c0c0c0", 65) + "c0", "depth", "65 nested objects: the default maxDepth is 64");
        b.Reject<InteropNested>("InteropNested", "object.reject.nested-arrays", Repeat("91", 15) + "c0", "type", "arrays where a string is expected, 15 deep");

        // ----- the user formatter type (D12): its own wire form, carried as raw bytes in the value model
        b.Both("InteropPoint", "external.point", new InteropPoint(3, -4), "InteropPointFormatter: [X, Y]");
        b.Both("List<InteropPoint>", "external.point-list", new List<InteropPoint> { new(0, 0), new(int.MaxValue, int.MinValue) });

        // ----- enums: the underlying integer
        foreach (var (name, value) in new[] { ("none", InteropEnum.None), ("one", InteropEnum.One), ("negative", InteropEnum.Negative), ("min", InteropEnum.Min), ("max", InteropEnum.Max), ("undefined", (InteropEnum)7) })
            b.Both("InteropEnum", "enum.int." + name, value, name == "undefined" ? "any value of the underlying type, named or not" : null);
        foreach (var (name, value) in new[] { ("none", InteropFlags.None), ("a-c", InteropFlags.A | InteropFlags.C), ("high", InteropFlags.High), ("all", InteropFlags.All) })
            b.Both("InteropFlags", "enum.ushort-flags." + name, value);
        foreach (var (name, value) in new[] { ("negative", InteropLongEnum.Negative), ("big", InteropLongEnum.Big), ("beyond-double", InteropLongEnum.BeyondDouble), ("min", InteropLongEnum.Min), ("max", InteropLongEnum.Max) })
            b.Both("InteropLongEnum", "enum.long." + name, value, name == "beyond-double" ? "2^53 + 1: a string in the value model" : null);
        b.Both("InteropEnum?", "enum.nullable", (InteropEnum?)InteropEnum.Two);
        b.Both<InteropEnum?>("InteropEnum?", "enum.nullable-nil", null);
        b.Both("InteropEnum[]", "enum.array", new[] { InteropEnum.One, InteropEnum.Min }, "an enum array is element-wise, not ext 78");
        b.Reject<InteropFlags>("InteropFlags", "enum.reject.range", "ce00010000", "range", "65536 is outside the ushort underlying type");
        b.Reject<InteropEnum>("InteropEnum", "enum.reject.string", "a3416e79", "type", "enums are never names on the wire");
    }

    private static void Unions(GoldenBuilder b)
    {
        b.File("unions");

        // ----- unions: [tag, value] (§10.3)
        b.Both("InteropShape", "union.circle", (InteropShape)new InteropCircle { Radius = 2 });
        b.Both("InteropShape", "union.rectangle", (InteropShape)new InteropRectangle { Width = 3, Height = 4 });
        b.Both<InteropShape?>("InteropShape", "union.nil", null);
        b.Both("IInteropMessage", "union.interface.text", (IInteropMessage)new InteropTextMessage { Text = "hi" }, "a union over an interface");
        b.Both("IInteropMessage", "union.interface.binary", (IInteropMessage)new InteropBinaryMessage { Data = [1, 2], Flags = InteropFlags.A | InteropFlags.High });
        b.Both("List<InteropShape>", "union.list", new List<InteropShape> { new InteropCircle { Radius = 0.5 }, new InteropRectangle(), new InteropCircle() });
        b.Decode<InteropShape>("InteropShape", "union.tag-from-uint16", "92cd000091cb3ff0000000000000", "the tag in a non-canonical integer form");
        b.Reject<InteropShape>("InteropShape", "union.reject.unknown-tag", "920590", "value", "no case has tag 5");
        b.Reject<InteropShape>("InteropShape", "union.reject.tag-range", "92ce0001000090", "range", "tags are uint16");
        b.Reject<InteropShape>("InteropShape", "union.reject.count-1", "9100", "count");
        b.Reject<InteropShape>("InteropShape", "union.reject.count-3", "930091cb3ff0000000000000c0", "count");
        b.Reject<InteropShape>("InteropShape", "union.reject.tag-type", "92a13091cb3ff0000000000000", "type");

        // Depth: the list, the union and the case object are one level each.
        b.Both("List<InteropShape>", "limit.depth.union", new List<InteropShape> { new InteropCircle { Radius = 1 } }, "3 levels with maxDepth 3", new GoldenOptions(MaxDepth: 3));
        b.Reject<List<InteropShape>>("List<InteropShape>", "limit.reject.depth-union", "91920091cb3ff0000000000000", "depth", "3 levels with maxDepth 2", new GoldenOptions(MaxDepth: 2));
    }
}
