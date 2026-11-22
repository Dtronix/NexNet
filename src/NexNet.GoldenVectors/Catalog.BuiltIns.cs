using System.Globalization;
using System.Numerics;

namespace NexNet.GoldenVectors;

public static partial class GoldenGenerator
{
    private static void DateAndTime(GoldenBuilder b)
    {
        const long ticks2020 = 637134336000000000; // 2020-01-01T00:00:00
        const long maxTicks = 3155378975999999999;
        const long utcFlag = 1L << 62;
        const long localFlag = unchecked((long)0x8000000000000000);

        // DateTime: ToBinary, the kind in bits 62-63 (§10.6).
        b.Both("DateTime", "datetime.unspecified", new DateTime(ticks2020, DateTimeKind.Unspecified));
        b.Both("DateTime", "datetime.utc", new DateTime(ticks2020, DateTimeKind.Utc));
        b.Both("DateTime", "datetime.local", DateTime.FromBinary(localFlag | ticks2020),
            "Local: bits 0-61 are the UTC ticks; each side shows the instant in its own time zone");
        b.Both("DateTime", "datetime.min", DateTime.MinValue);
        b.Both("DateTime", "datetime.max", DateTime.MaxValue);
        b.Both("DateTime", "datetime.max-utc", new DateTime(maxTicks, DateTimeKind.Utc));
        b.Both("DateTime", "datetime.unix-epoch-utc", DateTime.UnixEpoch);
        b.Both("DateTime", "datetime.ticks-utc", new DateTime(ticks2020 + 1234567, DateTimeKind.Utc), "sub-millisecond ticks");
        b.Decode<DateTime>("DateTime", "datetime.local.from-kind-11", "d3" + (unchecked((long)0xC000000000000000) | ticks2020).ToString("x16", CultureInfo.InvariantCulture),
            "kind bits 11 (.NET's ambiguous-DST flag) read as Local; writers use 10");
        b.Reject<DateTime>("DateTime", "datetime.reject.unspecified-above-max", "cf" + (maxTicks + 1).ToString("x16", CultureInfo.InvariantCulture), "value");
        b.Reject<DateTime>("DateTime", "datetime.reject.utc-above-max", "cf" + (utcFlag | (maxTicks + 1)).ToString("x16", CultureInfo.InvariantCulture), "value");
        b.Reject<DateTime>("DateTime", "datetime.reject.string", "a3323032", "type");

        // DateTimeOffset: [UtcTicks, offset minutes].
        foreach (var (name, minutes) in new[] { ("utc", 0), ("p0530", 330), ("m0800", -480), ("p1400", 840), ("m1400", -840) })
            b.Both("DateTimeOffset", "datetimeoffset." + name, new DateTimeOffset(ticks2020 + minutes * TimeSpan.TicksPerMinute, TimeSpan.FromMinutes(minutes)));
        b.Both("DateTimeOffset", "datetimeoffset.min", DateTimeOffset.MinValue);
        b.Both("DateTimeOffset", "datetimeoffset.max", DateTimeOffset.MaxValue);
        b.Reject<DateTimeOffset>("DateTimeOffset", "datetimeoffset.reject.offset-841", "9200cd0349", "value", "offsets are -840 to 840 minutes");
        b.Reject<DateTimeOffset>("DateTimeOffset", "datetimeoffset.reject.local-below-min", "9200d0c4", "value", "UtcTicks + offset must be within 0 to MaxTicks");
        b.Reject<DateTimeOffset>("DateTimeOffset", "datetimeoffset.reject.count", "9100", "count");

        // TimeSpan, DateOnly, TimeOnly.
        foreach (var (name, value) in new[] { ("0", TimeSpan.Zero), ("1-tick", new TimeSpan(1)), ("m1-tick", new TimeSpan(-1)), ("1-day", TimeSpan.FromDays(1)), ("max", TimeSpan.MaxValue), ("min", TimeSpan.MinValue) })
            b.Both("TimeSpan", "timespan." + name, value);
        b.Both("DateOnly", "dateonly.min", DateOnly.MinValue);
        b.Both("DateOnly", "dateonly.2020", new DateOnly(2020, 1, 1));
        b.Both("DateOnly", "dateonly.max", DateOnly.MaxValue);
        b.Reject<DateOnly>("DateOnly", "dateonly.reject.above-max", "ce0037b9db", "value");
        b.Reject<DateOnly>("DateOnly", "dateonly.reject.m1", "ff", "value");
        b.Both("TimeOnly", "timeonly.min", TimeOnly.MinValue);
        b.Both("TimeOnly", "timeonly.noon", new TimeOnly(12, 34, 56, 789));
        b.Both("TimeOnly", "timeonly.max", TimeOnly.MaxValue);
        b.Reject<TimeOnly>("TimeOnly", "timeonly.reject.one-day", "cf000000c92a69c000", "value");
        b.Reject<TimeOnly>("TimeOnly", "timeonly.reject.m1", "ff", "value");
    }

    private static void OtherBuiltIns(GoldenBuilder b)
    {
        // Guid: bin16 in RFC 4122 (big-endian) order.
        b.Both("Guid", "guid.empty", Guid.Empty);
        b.Both("Guid", "guid.byte-order", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), "the bytes in text order, not Guid.ToByteArray()");
        b.Both("Guid", "guid.v1", Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"));
        b.Decode<Guid>("Guid", "guid.from-str", "b0" + "00112233445566778899aabbccddeeff", "a str of 16 bytes is read as the bin");
        b.Reject<Guid>("Guid", "guid.reject.15-bytes", "c40f" + Repeat("00", 15), "size");
        b.Reject<Guid>("Guid", "guid.reject.17-bytes", "c411" + Repeat("00", 17), "size");

        // decimal: GetBits as four little-endian int32 (lo, mid, hi, flags).
        foreach (var (name, value, note) in new (string, decimal, string?)[]
                 {
                     ("0", 0m, null), ("1", 1m, null), ("m1", -1m, null), ("1.5", 1.5m, null), ("1.50", 1.50m, "the scale is kept: 1.50 is not 1.5"),
                     ("m1.5", -1.5m, "the specification example"), ("123.456", 123.456m, null), ("max", decimal.MaxValue, null), ("min", decimal.MinValue, null),
                     ("smallest", 0.0000000000000000000000000001m, "scale 28"), ("m0", new decimal(0, 0, 0, true, 0), "negative zero"),
                 })
            b.Both("decimal", "decimal." + name, value, note);
        b.Reject<decimal>("decimal", "decimal.reject.scale-29", "c410" + "01000000" + "00000000" + "00000000" + "00001d00", "value", "scales are 0 to 28");
        b.Reject<decimal>("decimal", "decimal.reject.reserved-bit", "c410" + "01000000" + "00000000" + "00000000" + "01000000", "value", "flags bits 0-15 and 24-30 are 0");
        b.Reject<decimal>("decimal", "decimal.reject.15-bytes", "c40f" + Repeat("00", 15), "size");

        // BigInteger: bin, little-endian two's complement, shortest form.
        foreach (var (name, value) in new (string, BigInteger)[]
                 {
                     ("0", BigInteger.Zero), ("1", BigInteger.One), ("m1", BigInteger.MinusOne), ("127", 127), ("128", 128), ("m128", -128), ("m129", -129),
                     ("255", 255), ("256", 256), ("i64-max", long.MaxValue), ("u64-max", ulong.MaxValue), ("2p64", BigInteger.One << 64),
                     ("m2p100", -(BigInteger.One << 100)), ("1e30", BigInteger.Pow(10, 30)),
                 })
            b.Both("BigInteger", "biginteger." + name, value);
        b.Decode<BigInteger>("BigInteger", "biginteger.1.non-minimal", "c4020100", "extra sign bytes are accepted");
        b.Decode<BigInteger>("BigInteger", "biginteger.0.empty", "c400", "an empty bin is 0");
        b.Reject<BigInteger>("BigInteger", "biginteger.reject.nil", "c0", "type");

        // Uri and Version: str.
        b.Both("Uri", "uri.absolute", new Uri("https://example.com/path?q=1#f"));
        b.Both("Uri", "uri.relative", new Uri("a/b?c", UriKind.Relative));
        b.Both("Uri", "uri.urn", new Uri("urn:isbn:0451450523"));
        b.Both<Uri?>("Uri", "uri.nil", null);
        b.Both("Version", "version.2", new Version(1, 2));
        b.Both("Version", "version.3", new Version(1, 2, 3));
        b.Both("Version", "version.4", new Version(1, 2, 3, 4));
        b.Both<Version?>("Version", "version.nil", null);
        b.Reject<Version>("Version", "version.reject.text", "a178", "value");
        b.Reject<Version>("Version", "version.reject.one-part", "a131", "value", "a Version has 2 to 4 parts");
    }
}
