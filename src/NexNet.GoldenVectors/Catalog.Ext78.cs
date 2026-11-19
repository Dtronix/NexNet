namespace NexNet.GoldenVectors;

public static partial class GoldenGenerator
{
    private static void Ext78(GoldenBuilder b)
    {
        b.File("ext78");

        // Ext 78: one kind byte, then the elements' little-endian bytes (§10.5).
        b.Both("sbyte[]", "ext78.sbyte.empty", Array.Empty<sbyte>(), "an empty array is fixext1: d4 4e <kind>");
        b.Both("sbyte[]", "ext78.sbyte.fixext2", new sbyte[] { -1 });
        b.Both("sbyte[]", "ext78.sbyte.ext8", new sbyte[] { -128, 127 }, "3 data bytes: no fixext fits");
        b.Both("sbyte[]", "ext78.sbyte.fixext4", new sbyte[] { -128, 0, 127 });
        b.Both("sbyte[]", "ext78.sbyte.fixext8", Enumerable.Range(1, 7).Select(i => (sbyte)i).ToArray());
        b.Both("sbyte[]", "ext78.sbyte.fixext16", Enumerable.Range(1, 15).Select(i => (sbyte)-i).ToArray());
        b.Both("short[]", "ext78.short", new short[] { -1, short.MaxValue, short.MinValue });
        b.Both("ushort[]", "ext78.ushort", new ushort[] { 1, ushort.MaxValue });
        b.Both("int[]", "ext78.int.empty", Array.Empty<int>());
        b.Both("int[]", "ext78.int", new[] { int.MinValue, 0, int.MaxValue });
        b.Both("int[]", "ext78.int.ext16", Enumerable.Range(0, 64).ToArray(), "257 data bytes: ext16");
        b.Both<int[]?>("int[]", "ext78.int.nil", null);
        b.Both("uint[]", "ext78.uint", new[] { 1u, uint.MaxValue });
        b.Both("long[]", "ext78.long", new[] { long.MinValue, -1L, long.MaxValue });
        b.Both("ulong[]", "ext78.ulong", new[] { 0UL, (ulong)long.MaxValue + 1, ulong.MaxValue });
        b.Both("float[]", "ext78.float", new[] { 1.5f, -0f, float.NaN, float.NegativeInfinity, float.Epsilon });
        b.Both("double[]", "ext78.double", new[] { 1.5d, -0d, double.NaN, double.PositiveInfinity, double.Epsilon });
        b.Both("char[]", "ext78.char", new[] { 'a', '\u5b57', '\ud800', '\uffff' }, "any UTF-16 code unit, including a lone surrogate");
        b.Both("half[]", "ext78.half", new[] { (Half)1, (Half)(-2.5), Half.NaN, Half.PositiveInfinity, Half.Epsilon }, "raw binary16 bits");
        b.Both("List<int>", "ext78.int-list", new List<int> { 1, -2, 3 }, "List<T> of an ext 78 element is ext 78 too");
        b.Both("Memory<long>", "ext78.long-memory", new Memory<long>(new[] { 1L, long.MinValue }));
        b.Both("ReadOnlyMemory<double>", "ext78.double-read-only-memory", new ReadOnlyMemory<double>(new[] { 0.25, -8d }));

        b.Decode<int[]>("int[]", "ext78.int.empty.from-ext8", "c7014e04");
        b.Decode<int[]>("int[]", "ext78.int.from-ext16", "c800054e0401000000");
        b.Decode<int[]>("int[]", "ext78.int.from-ext32", "c9000000054e0401000000");
        b.Decode<sbyte[]>("sbyte[]", "ext78.sbyte.from-ext8", "c7024e01ff", "fixext2 data in an ext8 header");

        b.Reject<int[]>("int[]", "ext78.reject.kind-mismatch", "c7094e060100000000000000", "type", "int64 elements for an int32 array");
        b.Reject<int[]>("int[]", "ext78.reject.partial-element", "c7044e04010203", "size", "the data after the kind byte is not a multiple of 4");
        b.Reject<int[]>("int[]", "ext78.reject.no-kind", "c7004e", "size", "no kind byte");
        b.Reject<int[]>("int[]", "ext78.reject.ext-type", "c7054d0401000000", "type", "ext type 77");
        b.Reject<int[]>("int[]", "ext78.reject.element-array", "920102", "type", "element-wise arrays are not read as ext 78");
        b.Reject<int[]>("int[]", "ext78.reject.length", "c7404e04", "length");
    }
}
