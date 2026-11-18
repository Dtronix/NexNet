namespace NexNet.Generator.Tests.Ir;

/// <summary>Sources the IR tests build. Copies of the integration test nexuses (interfaces only) plus a type zoo.</summary>
internal static class IrFixtures
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using NexNet;
        using NexNet.Collections;
        using NexNet.Collections.Lists;
        using NexNet.Pipes;
        using NexNet.Serialization;

        """;

    /// <summary>The integration tests' IClientNexus / IServerNexus pair.</summary>
    public const string Basic = Usings + """
        namespace NexNet.IntegrationTests.TestInterfaces;

        public partial interface IClientNexus
        {
            void ClientVoid();
            void ClientVoidWithParam(int id);
            ValueTask ClientTask();
            ValueTask ClientTaskWithParam(int data);
            ValueTask<int> ClientTaskValue();
            ValueTask<int> ClientTaskValueWithParam(int data);
            ValueTask ClientTaskWithCancellation(CancellationToken cancellationToken);
            ValueTask ClientTaskWithValueAndCancellation(int value, CancellationToken cancellationToken);
            ValueTask<int> ClientTaskValueWithCancellation(CancellationToken cancellationToken);
            ValueTask<int> ClientTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
            ValueTask ClientTaskValueWithDuplexPipe(INexusDuplexPipe pipe);

            ValueTask ClientData(byte[] data);
        }

        public partial interface IServerNexus
        {
            [NexusCollection(NexusCollectionMode.BiDirectional)]
            INexusList<int> IntListBi { get; }

            [NexusCollection(NexusCollectionMode.Relay)]
            INexusList<int> IntListRelay { get; }

            [NexusCollection(NexusCollectionMode.ServerToClient)]
            INexusList<int> IntListSC { get; }

            void ServerVoid();
            void ServerVoidWithParam(int id);
            ValueTask ServerTask();
            ValueTask ServerTaskWithParam(int data);
            ValueTask<int> ServerTaskValue();
            ValueTask<int> ServerTaskValueWithParam(int data);
            ValueTask ServerTaskWithCancellation(CancellationToken cancellationToken);
            ValueTask ServerTaskWithValueAndCancellation(int value, CancellationToken cancellationToken);
            ValueTask<int> ServerTaskValueWithCancellation(CancellationToken cancellationToken);
            ValueTask<int> ServerTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
            ValueTask ServerTaskValueWithDuplexPipe(INexusDuplexPipe pipe);

            ValueTask ServerData(byte[] data);

            ValueTask<byte[]> ServerDataResult(int size);

            ValueTask<byte[]> ServerEcho(byte[] data);
        }

        [Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
        public partial class ClientNexus
        {
        }

        [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
        public partial class ServerNexus
        {
        }
        """;

    /// <summary>The integration tests' versioned nexuses.</summary>
    public const string Versioned = Usings + """
        namespace NexNet.IntegrationTests.TestInterfaces;

        public partial interface ISimpleClientNexus
        {
            ValueTask Run();
        }

        [NexusVersion(Version = "v1.0", HashLock = -326045431)]
        public partial interface IVersionedServerNexusV1
        {
            [NexusMethod(1)]
            ValueTask<bool> VerifyVersionV1(string version);
        }

        [NexusVersion(Version = "v1.1", HashLock = -1058213897)]
        public partial interface IVersionedServerNexusV1_1 : IVersionedServerNexusV1
        {
            [NexusMethod(2)]
            void RunTaskV1_1();

            [NexusMethod(3)]
            ValueTask<ReturnState> RunTaskWithResultV1_1();

            public enum ReturnState : ushort
            {
                Unset = 0,
                Success = 1,
                Failure = 2,
            }
        }

        [NexusVersion(Version = "v1.2", HashLock = 1866304169)]
        public partial interface IVersionedServerNexusV2 : IVersionedServerNexusV1_1
        {
            [NexusMethod(4)]
            ValueTask RunTaskNewV2();
        }

        public partial interface INonVersionedServerNexus
        {
            ValueTask RunTaskNew();
            void RunTask();
            ValueTask<ReturnState> RunTaskWithResult();
            ValueTask<bool> VerifyVersion(string version);

            public enum ReturnState : ushort
            {
                Unset = 0,
                Success = 1,
                Failure = 2,
            }
        }

        [Nexus<ISimpleClientNexus, IVersionedServerNexusV1>(NexusType = NexusType.Client)]
        public partial class VersionedClientNexusV1 { }

        [Nexus<ISimpleClientNexus, IVersionedServerNexusV2>(NexusType = NexusType.Client)]
        public partial class VersionedClientNexusV2 { }

        [Nexus<ISimpleClientNexus, INonVersionedServerNexus>(NexusType = NexusType.Client)]
        public partial class NonVersionedClientNexus { }

        [Nexus<IVersionedServerNexusV2, ISimpleClientNexus>(NexusType = NexusType.Server)]
        public partial class VersionedServerNexusV2 { }

        [Nexus<IVersionedServerNexusV1_1, ISimpleClientNexus>(NexusType = NexusType.Server)]
        public partial class VersionedServerNexusV1_1 { }

        [Nexus<INonVersionedServerNexus, ISimpleClientNexus>(NexusType = NexusType.Server)]
        public partial class NonVersionedServerNexus { }
        """;

    /// <summary>Serialized types of every kind: unions, generics, enums, ext 78 arrays, recursion, literals, docs.</summary>
    public const string ZooTypes = Usings + """
        #nullable enable
        [assembly: NexusSerializable<Zoo.RuntimeOnly>]
        [assembly: NexusFormatter<Zoo.PointFormatter, Zoo.Point>]

        namespace Zoo;

        /// <summary>Primary colors. See <see cref="Shape"/>.</summary>
        public enum Color : byte
        {
            Red = 1,
            Green = 2,
            Blue = 4,
        }

        [Flags]
        public enum Access : ulong
        {
            None = 0,
            Read = 1,
            Write = 2,
            All = 0xFFFFFFFFFFFFFFFF,
        }

        public enum Perm
        {
            User,
            Admin,
        }

        /// <summary>
        /// A drawable shape with a <paramref name="ignored"/> reference.
        /// </summary>
        [NexusObject]
        [NexusUnion<Circle>(1)]
        [NexusUnion<Square>(2)]
        [NexusUnion<RoundedSquare>(3)]
        public abstract class Shape
        {
            /// <summary>The display name.</summary>
            [NexusKey(0)] public string Name { get; set; } = "shape";
        }

        [NexusObject]
        public class Circle : Shape
        {
            [NexusKey(1)] public double Radius { get; set; } = 1.5;
        }

        [NexusObject]
        public class Square : Shape
        {
            [NexusKey(1)] public int Side { get; set; } = -3;
        }

        [NexusObject]
        public class RoundedSquare : Square
        {
            [NexusKey(2)] public float Corner { get; set; }
        }

        [NexusObject]
        public class Envelope<T>
        {
            [NexusKey(0)] public T Value { get; set; } = default!;
            [NexusKey(1)] public T? Maybe { get; set; }
            [NexusKey(3)] public List<T>? History { get; set; }
        }

        /// <summary>A linked list node.</summary>
        [NexusObject]
        public class Node
        {
            [NexusKey(0)] public int Value;
            [NexusKey(1)] public Node? Next;
            [NexusKey(2)] public List<Tree>? Children;
        }

        [NexusObject]
        public class Tree
        {
            [NexusKey(0)] public Node? Parent;
        }

        [NexusObject]
        public record struct Pair
        {
            [NexusKey(0)] public int Left { get; init; }
            [NexusKey(1)] public int Right { get; init; }
        }

        public struct Point
        {
            public int X;
            public int Y;
        }

        public sealed class PointFormatter : NexusFormatter<Point>
        {
            public override void Serialize(ref MsgPackWriter writer, Point value) => throw new NotSupportedException();
            public override void Deserialize(ref MsgPackReader reader, ref Point value) => throw new NotSupportedException();
        }

        [NexusObject]
        public class RuntimeOnly
        {
            [NexusKey(0)] public Color Tint = Color.Blue;
        }

        [NexusObject]
        public class Orphan
        {
            [NexusKey(0)] public string? Text = null;
        }

        [NexusObject]
        public class Everything
        {
            [NexusKey(0)] public bool Flag { get; set; } = true;
            [NexusKey(1)] public byte[] Bytes { get; set; } = Array.Empty<byte>();
            [NexusKey(2)] public int[] Ints { get; set; } = Array.Empty<int>();
            [NexusKey(3)] public List<long> Longs { get; set; } = new();
            [NexusKey(4)] public ReadOnlyMemory<float> Floats { get; set; }
            [NexusKey(5)] public Memory<byte> Memory { get; set; }
            [NexusKey(6)] public List<byte> ByteList { get; set; } = new();
            [NexusKey(7)] public IReadOnlyList<string?> Names { get; set; } = Array.Empty<string?>();
            [NexusKey(8)] public HashSet<Guid> Ids { get; set; } = new();
            [NexusKey(9)] public Dictionary<string, Color> Colors { get; set; } = new();
            [NexusKey(10)] public Queue<int> Queue { get; set; } = new();
            [NexusKey(11)] public Stack<string> Stack { get; set; } = new();
            [NexusKey(12)] public (int, string) Tuple { get; set; }
            [NexusKey(13)] public Tuple<int, Node>? RefTuple { get; set; }
            [NexusKey(14)] public KeyValuePair<string, int> Kvp { get; set; }
            [NexusKey(15)] public int? MaybeInt { get; set; }
            [NexusKey(16)] public Color Color { get; set; } = Color.Green;
            [NexusKey(17)] public Access Access { get; set; } = Access.Read | Access.Write;
            [NexusKey(18)] public decimal Price = 1.25m;
            [NexusKey(19)] public char Letter = 'x';
            [NexusKey(20)] public DateTimeOffset When;
            [NexusKey(21)] public TimeSpan Span;
            [NexusKey(22)] public DateOnly Day;
            [NexusKey(23)] public TimeOnly Time;
            [NexusKey(24)] public Half Small;
            [NexusKey(25)] public System.Numerics.BigInteger Huge;
            [NexusKey(26)] public Uri? Link;
            [NexusKey(27)] public Version? Release;
            [NexusKey(28)] public Envelope<Shape?>? Wrapped;
            [NexusKey(29)] public Point Point;
            [NexusKey(30)] internal string Secret = string.Empty;
            [NexusKey(31)] public string? Note = null;
            [NexusKey(32)] public long Big = -9000000000;
            [NexusKey(33)] public Pair Pair;
            [NexusKey(34)] public Half[]? Halves;
            [NexusKey(35)] public DateTime Stamp;
        }
        """;

    /// <summary>A nexus pair over the zoo types: pipes, channels, collections, ids, separate pool, authorization.</summary>
    public const string ZooNexus = Usings + """
        #nullable enable
        namespace Zoo;

        /// <summary>The zoo server API.</summary>
        public interface IZooServer
        {
            /// <summary>Echoes <paramref name="value"/> back.</summary>
            ValueTask<Everything> Echo(Everything value, CancellationToken cancellationToken);
            void Fire(Shape? shape, int? count);
            ValueTask<Envelope<int>> Wrap(int value);
            ValueTask Upload(INexusDuplexPipe pipe);
            ValueTask Stream(INexusDuplexChannel<Node> channel);
            [NexusMethod(SeparatePool = true)] ValueTask Slow();
            [NexusMethod(50)] ValueTask<string?> Fixed(string? text);
            [NexusMethod(Ignore = true)] void Skipped(int value);
            ValueTask<Dictionary<Color, List<Shape>>> Lookup(Access access);

            /// <summary>Every shape on the server.</summary>
            [NexusCollection(NexusCollectionMode.ServerToClient)]
            INexusList<Shape> Shapes { get; }
        }

        public interface IZooClient
        {
            ValueTask<int> Ping(int value);
            void Notify(Color color);
        }

        /// <summary>Serves the zoo.</summary>
        [Nexus<IZooServer, IZooClient>(NexusType = NexusType.Server)]
        public partial class ZooServerNexus
        {
            [NexusAuthorize<Perm>(Perm.Admin)]
            public ValueTask Slow() => default;
        }

        [Nexus<IZooClient, IZooServer>(NexusType = NexusType.Client)]
        public partial class ZooClientNexus
        {
        }
        """;
}
