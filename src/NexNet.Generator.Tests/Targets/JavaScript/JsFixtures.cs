namespace NexNet.Generator.Tests.Targets.JavaScript;

/// <summary>Sources for the JavaScript emitter tests.</summary>
internal static class JsFixtures
{
    private const string Usings = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Numerics;
        using System.Threading.Tasks;
        using NexNet;
        using NexNet.Serialization;

        """;

    /// <summary>The interop type zoo (H02) with a nexus pair that opts in to JavaScript; the golden vectors use these types.</summary>
    public static string[] Zoo() => new[]
    {
        // The TestNexus project has implicit usings.
        "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\n"
        + File.ReadAllText(Path.Combine(JsTestRunner.RepoRoot(), "src", "NexNet.TestNexus", "Interop", "InteropTypes.cs")),
        Usings + """
            using NexNet.TestNexus.Interop;

            namespace ZooTest;

            public interface IZooClient { ValueTask Tell(InteropItem item); }
            public interface IZooServer { ValueTask<InteropNested> Echo(InteropNested value, List<InteropPoint> points); }

            [Nexus<IZooClient, IZooServer>(NexusType = NexusType.Client)]
            public partial class ZooClientNexus { public ValueTask Tell(InteropItem item) => default; }

            [Nexus<IZooServer, IZooClient>(NexusType = NexusType.Server, Targets = NexusTargets.JavaScript)]
            public partial class ZooServerNexus { public ValueTask<InteropNested> Echo(InteropNested value, List<InteropPoint> points) => new(value); }
            """,
    };

    /// <summary>
    /// Every shape the JavaScript emitter handles: initializer literals, struct defaults, generics whose sequences change wire
    /// form when bound, recursive and mutually recursive generics, a union with a case hierarchy, an interface union
    /// with a struct case, nested types, enums of every width, containers and a user formatter.
    /// </summary>
    public const string Features = Usings + """
        [assembly: NexusFormatter<Feat.MoneyFormatter, Feat.Money>]
        [assembly: NexusTargetFormatter<Feat.Money>(NexusTargets.JavaScript, "./ext/money-formatter.js#MoneyFormatter")]

        namespace Feat;

        public readonly record struct Money(long Cents);

        public sealed class MoneyFormatter : NexusFormatter<Money>
        {
            public override void Serialize(ref MsgPackWriter writer, Money value) => writer.Write(value.Cents);
            public override void Deserialize(ref MsgPackReader reader, ref Money value) => value = new Money(reader.ReadInt64());
        }

        /// <summary>Small flags & <bits>.</summary>
        [Flags] public enum Small : byte { None = 0, A = 1, B = 2, AB = 3, Top = 128 }
        public enum Huge : ulong { Zero = 0, Mid = 1UL << 63, Max = ulong.MaxValue }
        public enum Neg : sbyte { M = -128, Z = 0, P = 127 }
        [Flags] public enum Wide : long { None = 0, Low = 1, Sign = long.MinValue }

        [NexusObject]
        public class Defaults
        {
            [NexusKey(0)] public int Count { get; set; } = 5;
            [NexusKey(1)] public string? Name { get; set; } = "a\"b\\c\né";
            [NexusKey(2)] public double Ratio { get; set; } = 1.5;
            [NexusKey(3)] public float Single { get; set; } = 2.25f;
            [NexusKey(4)] public decimal Price { get; set; } = 1.25m;
            [NexusKey(5)] public Small Mode { get; set; } = Small.B;
            [NexusKey(6)] public char Letter { get; set; } = 'é';
            [NexusKey(7)] public long Big { get; set; } = 1099511627776;
            [NexusKey(8)] public ulong Unsigned { get; set; } = 18446744073709551615;
            [NexusKey(9)] public bool Flag { get; set; } = true;
            [NexusKey(10)] public short Short { get; set; } = -3;
            [NexusKey(11)] public DateTime When { get; set; }
            [NexusKey(12)] public (int, string?) Pair { get; set; }
            [NexusKey(13)] public KeyValuePair<string, int> Entry { get; set; }
            [NexusKey(14)] public Memory<int> Memory { get; set; }
            [NexusKey(15)] public int? Maybe { get; set; }
            [NexusKey(17)] public Guid Id { get; set; }
            [NexusKey(18)] public sbyte Tiny { get; set; } = -7;
            [NexusKey(19)] public uint Count32 { get; set; } = 4000000000;
            [NexusKey(20)] public Neg Signed { get; set; } = Neg.M;
        }

        [NexusObject]
        public class Holder<T>
        {
            [NexusKey(0)] public List<T>? Items { get; set; }
            [NexusKey(1)] public T[]? Array { get; set; }
            [NexusKey(2)] public IList<T>? Plain { get; set; }
            [NexusKey(3)] public T Value { get; set; } = default!;
        }

        [NexusObject]
        public class Tree<T>
        {
            [NexusKey(0)] public T Value { get; set; } = default!;
            [NexusKey(1)] public List<Tree<T>>? Children { get; set; }
        }

        [NexusObject]
        public class Ping<T>
        {
            [NexusKey(0)] public Pong<T>? Next { get; set; }
            [NexusKey(1)] public T Value { get; set; } = default!;
        }

        [NexusObject]
        public class Pong<T>
        {
            [NexusKey(0)] public Ping<T>? Next { get; set; }
        }

        [NexusObject]
        [NexusUnion<Circle>(0)]
        [NexusUnion<BigCircle>(1)]
        [NexusUnion<Square>(2)]
        public abstract class Shape { }

        [NexusObject] public class Circle : Shape { [NexusKey(0)] public double Radius { get; set; } }
        [NexusObject] public class BigCircle : Circle { [NexusKey(1)] public string? Label { get; set; } }
        [NexusObject] public class Square : Shape { [NexusKey(0)] public int Side { get; set; } }

        [NexusObject]
        [NexusUnion<Note>(0)]
        [NexusUnion<Spot>(1)]
        public interface IEvent { }

        [NexusObject] public class Note : IEvent { [NexusKey(0)] public string? Text { get; set; } }
        [NexusObject] public struct Spot : IEvent { [NexusKey(0)] public int X { get; set; } [NexusKey(1)] public Money Cost { get; set; } }

        public class Outer
        {
            [NexusObject]
            public class Inner
            {
                [NexusKey(0)] public Queue<int>? Queue { get; set; }
                [NexusKey(1)] public Stack<string>? Stack { get; set; }
                [NexusKey(2)] public HashSet<Small>? Set { get; set; }
                [NexusKey(3)] public Dictionary<string, Inner>? Map { get; set; }
            }
        }

        [NexusObject]
        public class Empty { }

        [NexusObject]
        public class Everything
        {
            [NexusKey(0)] public Tuple<int, string>? RefTuple { get; set; }
            [NexusKey(1)] public Half Half { get; set; }
            [NexusKey(2)] public Huge Huge { get; set; }
            [NexusKey(3)] public List<Money>? Monies { get; set; }
            [NexusKey(4)] public Shape? Shape { get; set; }
            [NexusKey(5)] public IEvent? Event { get; set; }
            [NexusKey(6)] public Tree<string>? Tree { get; set; }
            [NexusKey(7)] public Ping<int>? Ping { get; set; }
            [NexusKey(8)] public Holder<int>? Ints { get; set; }
            [NexusKey(9)] public Holder<byte>? Bytes { get; set; }
            [NexusKey(10)] public Holder<string>? Strings { get; set; }
            [NexusKey(11)] public Outer.Inner? Inner { get; set; }
            [NexusKey(12)] public Defaults? Defaults { get; set; }
            [NexusKey(13)] public byte[]? Bin { get; set; }
            [NexusKey(14)] public ushort[]? UShorts { get; set; }
            [NexusKey(15)] public Half[]? Halves { get; set; }
            [NexusKey(16)] public Version? Version { get; set; }
            [NexusKey(17)] public Uri? Uri { get; set; }
            [NexusKey(18)] public DateTimeOffset Dto { get; set; }
            [NexusKey(19)] public TimeSpan Span { get; set; }
            [NexusKey(20)] public DateOnly Date { get; set; }
            [NexusKey(21)] public TimeOnly Time { get; set; }
            [NexusKey(22)] public BigInteger Integer { get; set; }
            [NexusKey(23)] public Spot Spot { get; set; }
            [NexusKey(24)] public Spot? MaybeSpot { get; set; }
            [NexusKey(25)] public Money Money { get; set; }
            [NexusKey(26)] public Wide Wide { get; set; }
            [NexusKey(27)] public Empty? Empty { get; set; }
            [NexusKey(28)] public (int, Small, Money) Triple { get; set; }
            [NexusKey(29)] public Holder<ushort>? UShortHolder { get; set; }
            [NexusKey(30)] public Holder<Half>? HalfHolder { get; set; }
        }

        public interface IFeatClient { ValueTask Tell(Everything value); }
        public interface IFeatServer { ValueTask<Everything> Echo(Everything value); }

        [Nexus<IFeatClient, IFeatServer>(NexusType = NexusType.Client)]
        public partial class FeatClientNexus { public ValueTask Tell(Everything value) => default; }

        [Nexus<IFeatServer, IFeatClient>(NexusType = NexusType.Server, Targets = NexusTargets.JavaScript)]
        public partial class FeatServerNexus { public ValueTask<Everything> Echo(Everything value) => new(value); }

        /// <summary>Values serialized by C# and replayed by the Node runner (name, serialized type, value).</summary>
        public static class Samples
        {
            private static (string Name, Type Type, object? Value) S<T>(string name, T value) => (name, typeof(T), value);

            public static List<(string Name, Type Type, object? Value)> All()
            {
                var tree = new Tree<string> { Value = "root", Children = new() { new Tree<string> { Value = "leaf" } } };
                return new()
                {
                    S("Defaults.new", new Defaults()),
                    S("Everything.new", new Everything()),
                    S("Empty.new", new Empty()),
                    S("Defaults.set", new Defaults
                    {
                        Count = -1, Name = null, Ratio = double.NaN, Single = -0f, Price = -79228162514264337593543950335m,
                        Mode = Small.A | Small.Top, Letter = '\uffff', Big = long.MinValue, Unsigned = 1, Flag = false, Short = short.MaxValue,
                        When = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), Pair = (3, "x"), Entry = new("k", 9),
                        Memory = new[] { 1, -1 }, Maybe = 4, Id = new Guid("00112233-4455-6677-8899-aabbccddeeff"),
                        Tiny = sbyte.MinValue, Count32 = uint.MaxValue, Signed = (Neg)5,
                    }),
                    S("Holder<int>", new Holder<int> { Items = new() { 1, 2, 3 }, Array = new[] { 4, -5 }, Plain = new List<int> { 6 }, Value = 7 }),
                    S("Holder<byte>", new Holder<byte> { Items = new() { 1, 255 }, Array = new byte[] { 0, 200 }, Plain = new List<byte> { 9 }, Value = 8 }),
                    S("Holder<string>", new Holder<string> { Items = new() { "a", null! }, Array = new[] { "b" }, Plain = new List<string>(), Value = "v" }),
                    S("Holder<ushort>", new Holder<ushort> { Items = new() { 65535 }, Array = new ushort[] { 1 }, Value = 2 }),
                    S("Holder<Half>", new Holder<Half> { Items = new() { (Half)1.5f }, Array = new[] { (Half)(-2f) }, Value = Half.MaxValue }),
                    S("Tree<string>", tree),
                    S("Ping<int>", new Ping<int> { Value = 1, Next = new Pong<int> { Next = new Ping<int> { Value = 2 } } }),
                    S<Shape>("Shape.circle", new Circle { Radius = 1.5 }),
                    S<Shape>("Shape.big", new BigCircle { Radius = 2, Label = "big" }),
                    S<Shape>("Shape.square", new Square { Side = 3 }),
                    S<IEvent>("IEvent.note", new Note { Text = "n" }),
                    S<IEvent>("IEvent.spot", new Spot { X = 4, Cost = new Money(250) }),
                    S("Inner", new Outer.Inner
                    {
                        Queue = new Queue<int>(new[] { 1, 2 }), Stack = new Stack<string>(new[] { "bottom", "top" }),
                        Set = new HashSet<Small> { Small.A, (Small)64 }, Map = new() { ["self"] = new Outer.Inner() },
                    }),
                    S("Everything.set", new Everything
                    {
                        RefTuple = Tuple.Create(1, "one"), Half = (Half)0.5f, Huge = Huge.Max, Monies = new() { new Money(-1) },
                        Shape = new BigCircle { Label = "s" }, Event = new Spot { X = 1 }, Tree = tree,
                        Ints = new Holder<int> { Value = 1 }, Bytes = new Holder<byte> { Array = new byte[] { 7 } }, Inner = new Outer.Inner(),
                        Defaults = new Defaults(), Bin = new byte[] { 1, 2 }, UShorts = new ushort[] { 65535 }, Halves = new[] { (Half)1 },
                        Version = new Version(1, 2, 3), Uri = new Uri("https://example.com/a"), Dto = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.FromHours(-5)),
                        Span = TimeSpan.FromMilliseconds(-1500), Date = new DateOnly(1999, 12, 31), Time = new TimeOnly(23, 59), Integer = BigInteger.Parse("-123456789012345678901234567890"),
                        Spot = new Spot { X = -1, Cost = new Money(1) }, MaybeSpot = new Spot(), Money = new Money(long.MaxValue), Wide = Wide.Low | Wide.Sign,
                        Empty = new Empty(), Triple = (1, Small.AB, new Money(3)),
                    }),
                };
            }
        }
        """;
}