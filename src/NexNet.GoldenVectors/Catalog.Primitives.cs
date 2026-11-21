using System.Globalization;
using System.Numerics;

namespace NexNet.GoldenVectors;

public static partial class GoldenGenerator
{
    private const long Pow53 = 1L << 53;

    private static readonly long[] SignedBoundaries =
    [
        0, 1, 31, 32, 127, 128, 255, 256, 32767, 32768, 65535, 65536, int.MaxValue, (long)int.MaxValue + 1, uint.MaxValue,
        (long)uint.MaxValue + 1, Pow53 - 1, Pow53, Pow53 + 1, long.MaxValue,
        -1, -31, -32, -33, -127, -128, -129, -32767, -32768, -32769, int.MinValue, (long)int.MinValue - 1, -(Pow53 - 1), -Pow53,
        -(Pow53 + 1), long.MinValue,
    ];

    private static readonly ulong[] UnsignedBoundaries =
    [
        0, 1, 127, 128, 255, 256, 65535, 65536, uint.MaxValue, (ulong)uint.MaxValue + 1, Pow53 - 1, Pow53, Pow53 + 1, long.MaxValue,
        (ulong)long.MaxValue + 1, ulong.MaxValue,
    ];

    /// <summary>An id part for an integer: <c>m</c> for minus.</summary>
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture).Replace("-", "m", StringComparison.Ordinal);

    private static string N(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Repeat(string hexByte, int count) => string.Concat(Enumerable.Repeat(hexByte, count));

    private static void Primitives(GoldenBuilder b)
    {
        b.File("primitives");

        // ----- nil and bool
        b.Both<string?>("string", "nil", null, "nil (c0) is null for any reference or nullable type");
        b.Both("bool", "bool.false", false);
        b.Both("bool", "bool.true", true);

        // ----- integers: every MessagePack size boundary, 2^53 and the 64-bit extremes
        foreach (var v in SignedBoundaries)
            b.Both("long", "i64." + N(v), v);
        foreach (var v in SignedBoundaries.Where(v => v is >= int.MinValue and <= int.MaxValue))
            b.Both("int", "i32." + N(v), (int)v);
        foreach (var v in SignedBoundaries.Where(v => v is >= short.MinValue and <= short.MaxValue))
            b.Both("short", "i16." + N(v), (short)v);
        foreach (var v in SignedBoundaries.Where(v => v is >= sbyte.MinValue and <= sbyte.MaxValue))
            b.Both("sbyte", "i8." + N(v), (sbyte)v);
        foreach (var v in UnsignedBoundaries)
            b.Both("ulong", "u64." + N(v), v, v > long.MaxValue ? "above Long.MAX_VALUE" : v > (ulong)(Pow53 - 1) ? "above 2^53 - 1" : null);
        foreach (var v in UnsignedBoundaries.Where(v => v <= uint.MaxValue))
            b.Both("uint", "u32." + N(v), (uint)v);
        foreach (var v in UnsignedBoundaries.Where(v => v <= ushort.MaxValue))
            b.Both("ushort", "u16." + N(v), (ushort)v);
        foreach (var v in UnsignedBoundaries.Where(v => v <= byte.MaxValue))
            b.Both("byte", "u8." + N(v), (byte)v);

        // Readers accept every integer form whose value is in range (§10.1).
        string[] forms = ["cc01", "cd0001", "ce00000001", "cf0000000000000001", "d001", "d10001", "d200000001", "d30000000000000001"];
        foreach (var form in forms)
        {
            b.Decode<int>("int", "i32.1.from-" + form.Substring(0, 2), form);
            b.Decode<ulong>("ulong", "u64.1.from-" + form.Substring(0, 2), form);
            b.Decode<byte>("byte", "u8.1.from-" + form.Substring(0, 2), form);
        }

        b.Decode<long>("long", "i64.m1.from-d1", "d1ffff");
        b.Decode<long>("long", "i64.m1.from-d3", "d3ffffffffffffffff");
        b.Decode<sbyte>("sbyte", "i8.m1.from-d2", "d2ffffffff");
        b.Decode<long>("long", "i64.max.from-d3", "d37fffffffffffffff", "writers use the unsigned family for non-negative values");
        b.Decode<ulong>("ulong", "u64.max-i64.from-d3", "d37fffffffffffffff", "int64 form of a non-negative value");
        b.Decode<int>("int", "i32.127.from-d0", "d07f");

        b.Reject<byte>("byte", "u8.reject.256", "cd0100", "range");
        b.Reject<byte>("byte", "u8.reject.m1", "ff", "range");
        b.Reject<sbyte>("sbyte", "i8.reject.128", "cc80", "range");
        b.Reject<sbyte>("sbyte", "i8.reject.m129", "d1ff7f", "range");
        b.Reject<short>("short", "i16.reject.32768", "cd8000", "range");
        b.Reject<ushort>("ushort", "u16.reject.65536", "ce00010000", "range");
        b.Reject<ushort>("ushort", "u16.reject.m1", "ff", "range");
        b.Reject<int>("int", "i32.reject.2147483648", "ce80000000", "range");
        b.Reject<int>("int", "i32.reject.m2147483649", "d3ffffffff7fffffff", "range");
        b.Reject<uint>("uint", "u32.reject.m1", "ff", "range");
        b.Reject<uint>("uint", "u32.reject.4294967296", "cf0000000100000000", "range");
        b.Reject<long>("long", "i64.reject.9223372036854775808", "cf8000000000000000", "range", "uint64 above Long.MAX_VALUE");
        b.Reject<ulong>("ulong", "u64.reject.m1", "ff", "range");
        b.Reject<ulong>("ulong", "u64.reject.m1.from-d3", "d3ffffffffffffffff", "range");
        b.Reject<int>("int", "i32.reject.float", "ca3f800000", "type", "integers are never read from floats");
        b.Reject<int>("int", "i32.reject.string", "a131", "type");
        b.Reject<int>("int", "i32.reject.nil", "c0", "type", "nil is not a value of a non-nullable type");
        b.Reject<int>("int", "i32.reject.bool", "c3", "type");
        b.Reject<int>("int", "i32.reject.empty", "", "truncated");
        b.Reject<int>("int", "i32.reject.truncated", "cd01", "truncated");
        b.Reject<int>("int", "i32.reject.trailing", "0100", "trailing", "bytes after the value");
        b.Reject<int>("int", "i32.reject.c1", "c1", "reserved", "0xc1 is never used");
        b.Reject<bool>("bool", "bool.reject.int", "01", "type");

        // ----- char: one UTF-16 code unit as a canonical uint16
        foreach (var c in new[] { '\0', 'a', '\u007f', '\u0080', '\u00ff', '\u0100', '\u5b57', '\ud800', '\udfff', '\uffff' })
            b.Both("char", "char." + ((int)c).ToString("x4", CultureInfo.InvariantCulture), c, c is >= '\ud800' and <= '\udfff' ? "lone surrogate" : null);
        b.Reject<char>("char", "char.reject.65536", "ce00010000", "range");
        b.Reject<char>("char", "char.reject.m1", "ff", "range");

        // ----- float32 and float64 (bit patterns, so -0 and NaN survive)
        foreach (var (name, value) in new (string, float)[]
                 {
                     ("0", 0f), ("m0", -0f), ("1.5", 1.5f), ("m3.25", -3.25f), ("0.1", 0.1f), ("max", float.MaxValue), ("min", float.MinValue),
                     ("epsilon", float.Epsilon), ("min-normal", 1.17549435E-38f), ("inf", float.PositiveInfinity), ("m-inf", float.NegativeInfinity),
                     ("nan", float.NaN), ("nan-positive", BitConverter.UInt32BitsToSingle(0x7FC00000)),
                 })
            b.Both("float", "f32." + name, value, name.StartsWith("nan", StringComparison.Ordinal) ? "NaN bits are kept" : null);
        foreach (var (name, value) in new (string, double)[]
                 {
                     ("0", 0d), ("m0", -0d), ("1.5", 1.5d), ("m3.25", -3.25d), ("0.1", 0.1d), ("max", double.MaxValue), ("min", double.MinValue),
                     ("epsilon", double.Epsilon), ("2p53p1", Pow53 + 1d), ("inf", double.PositiveInfinity), ("m-inf", double.NegativeInfinity),
                     ("nan", double.NaN), ("nan-positive", BitConverter.UInt64BitsToDouble(0x7FF8000000000000)),
                 })
            b.Both("double", "f64." + name, value, name.StartsWith("nan", StringComparison.Ordinal) ? "NaN bits are kept" : null);

        // A float is always float32 and a double float64; readers also take the other width and integers.
        b.Decode<float>("float", "f32.1.5.from-f64", "cb3ff8000000000000");
        b.Decode<float>("float", "f32.0.1.from-f64", "cb3fb999999999999a", "narrowed to the nearest float32");
        b.Decode<float>("float", "f32.5.from-int", "05");
        b.Decode<float>("float", "f32.m1.from-int", "ff");
        b.Decode<float>("float", "f32.from-u64", "cfffffffffffffffff");
        b.Decode<double>("double", "f64.1.5.from-f32", "ca3fc00000");
        b.Decode<double>("double", "f64.5.from-int", "05");
        b.Decode<double>("double", "f64.from-u64", "cfffffffffffffffff");
        b.Decode<double>("double", "f64.from-i64-min", "d38000000000000000");
        b.Reject<float>("float", "f32.reject.nil", "c0", "type");
        b.Reject<double>("double", "f64.reject.string", "a131", "type");
        b.Reject<double>("double", "f64.reject.truncated", "cb3ff8", "truncated");

        // ----- Half: written as float32
        foreach (var (name, value) in new (string, Half)[]
                 {
                     ("0", (Half)0), ("m0", BitConverter.UInt16BitsToHalf(0x8000)), ("1", (Half)1), ("m2.5", (Half)(-2.5)), ("max", Half.MaxValue),
                     ("min", Half.MinValue), ("epsilon", Half.Epsilon), ("inf", Half.PositiveInfinity), ("m-inf", Half.NegativeInfinity), ("nan", Half.NaN),
                 })
            b.Both("half", "f16." + name, value);
        b.Decode<Half>("half", "f16.1.5.from-f64", "cb3ff8000000000000");
        b.Decode<Half>("half", "f16.1.from-f32-near", "ca3f800001", "the nearest Half");
        b.Decode<Half>("half", "f16.tie-down", "ca3f801000", "1 + 2^-11: a tie, rounds to even (1)");
        b.Decode<Half>("half", "f16.tie-up", "ca3f803000", "1 + 3 * 2^-11: a tie, rounds to even (1 + 2^-9)");
        b.Decode<Half>("half", "f16.overflow", "ca47888000", "70000 is beyond the Half range: infinity");
        b.Decode<Half>("half", "f16.5.from-int", "05");

        // ----- strings
        b.Both("string", "str.empty", "");
        b.Both("string", "str.a", "a");
        foreach (var length in new[] { 31, 32, 255, 256, 65535, 65536 })
            b.Both("string", "str.ascii-" + length, new string('a', length), length switch
            {
                31 => "longest fixstr",
                32 => "shortest str8",
                255 => "longest str8",
                256 => "shortest str16",
                65535 => "longest str16",
                _ => "shortest str32",
            });
        b.Both("string", "str.latin", "héllo wörld");
        b.Both("string", "str.cjk", "日本語のテキスト");
        b.Both("string", "str.emoji", "emoji 😀", "a 4-byte UTF-8 sequence (a surrogate pair in UTF-16)");
        b.Both("string", "str.nul", "a\0b", "an embedded U+0000");
        b.Both("string", "str.utf8-lengths", "\u007f\u0080\u07ff\u0800\uffff", "the UTF-8 length boundaries");
        b.Decode<string>("string", "str.abc.from-str8", "d903616263");
        b.Decode<string>("string", "str.abc.from-str16", "da0003616263");
        b.Decode<string>("string", "str.abc.from-str32", "db00000003616263");

        // Invalid UTF-8 is rejected by default and replaced with U+FFFD when StrictUtf8 is off (Unicode "maximal
        // subpart" replacement, as .NET does).
        var lenient = new GoldenOptions(StrictUtf8: false);
        foreach (var (name, hex, note) in new[]
                 {
                     ("lone-ff", "a1ff", "0xff never appears in UTF-8"),
                     ("lone-continuation", "a3618062", "a continuation byte without a lead byte"),
                     ("truncated-3", "a2e282", "a 3-byte sequence cut short"),
                     ("overlong", "a2c0af", "an overlong encoding of '/'"),
                     ("surrogate", "a3eda080", "an encoded UTF-16 surrogate (U+D800)"),
                     ("above-max", "a4f4908080", "above U+10FFFF"),
                 })
        {
            b.Reject<string>("string", "str.reject.utf8-" + name, hex, "utf8", note);
            b.Decode<string>("string", "str.utf8-" + name + ".lenient", hex, note + "; replaced", lenient);
        }

        b.Decode<string>("string", "str.utf8-lone-ff.trusted", "a1ff", "the trusted options do not check UTF-8", new GoldenOptions("trusted"));
        b.Reject<string>("string", "str.reject.int", "01", "type");
        b.Reject<string>("string", "str.reject.bin", "c40161", "type", "bin is not a string");
        b.Reject<string>("string", "str.reject.length", "a5616263", "length", "the length runs past the input");
        b.Reject<string>("string", "str.reject.str32-length", "dbffffffff61", "length");

        // ----- bin
        b.Both<byte[]?>("byte[]", "bin.nil", null);
        b.Both("byte[]", "bin.empty", Array.Empty<byte>());
        b.Both("byte[]", "bin.4", new byte[] { 1, 2, 3, 4 });
        foreach (var length in new[] { 255, 256, 65535, 65536 })
            b.Both("byte[]", "bin.len-" + length, Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray(), length switch
            {
                255 => "longest bin8",
                256 => "shortest bin16",
                65535 => "longest bin16",
                _ => "shortest bin32",
            });
        b.Both("Memory<byte>", "bin.memory", new Memory<byte>(new byte[] { 9, 8, 7 }), "Memory<byte> and the other byte containers are bin too");
        b.Both("ReadOnlyMemory<byte>", "bin.read-only-memory", new ReadOnlyMemory<byte>(new byte[] { 9, 8, 7 }));
        b.Both("ArraySegment<byte>", "bin.array-segment", new ArraySegment<byte>(new byte[] { 0, 9, 8, 7, 0 }, 1, 3));
        b.Both("ReadOnlySequence<byte>", "bin.read-only-sequence", new System.Buffers.ReadOnlySequence<byte>(new byte[] { 9, 8, 7 }));
        b.Decode<byte[]>("byte[]", "bin.1.from-bin16", "c500010a");
        b.Decode<byte[]>("byte[]", "bin.1.from-bin32", "c6000000010a");
        b.Decode<byte[]>("byte[]", "bin.from-fixstr", "a3010203", "str forms are read as bin (old-spec writers)");
        b.Decode<byte[]>("byte[]", "bin.from-str8", "d903010203");
        b.Reject<byte[]>("byte[]", "bin.reject.array", "93010203", "type");
        b.Reject<byte[]>("byte[]", "bin.reject.length", "c40501", "length");
        b.Reject<byte[]>("byte[]", "bin.reject.bin32-length", "c67fffffff", "length", "the length is checked before anything is allocated");

        DateAndTime(b);
        OtherBuiltIns(b);
    }
}
