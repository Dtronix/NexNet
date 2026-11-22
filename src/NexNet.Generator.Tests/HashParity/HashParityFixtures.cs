using NexNet.Generator.Tests.Ir;

namespace NexNet.Generator.Tests.HashParity;

/// <summary>
/// Fixture sources for the hash parity tests: each pairs C# client nexus classes with a server nexus class that opts in
/// to the foreign targets, so one compilation yields both the IR export and the compiled C# client.
/// </summary>
internal static class HashParityFixtures
{
    private const string Targets = ", Targets = NexusTargets.Java | NexusTargets.JavaScript";

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

    /// <summary>Opts every server nexus class of <paramref name="source"/> in to the targets.</summary>
    private static string OptIn(string source)
        => source.Replace("(NexusType = NexusType.Server)", "(NexusType = NexusType.Server" + Targets + ")", StringComparison.Ordinal);

    /// <summary>The integration tests' plain pair: methods of every shape, collections and a duplex pipe.</summary>
    public static string[] Plain() => new[] { OptIn(IrFixtures.Basic) };

    /// <summary>
    /// The integration tests' versioned nexuses: servers at v1.2 (V2), v1.1 and unversioned, each with the client that
    /// targets it.
    /// </summary>
    public static string[] Versioned() => new[]
    {
        OptIn(IrFixtures.Versioned),
        Usings + """
            namespace NexNet.IntegrationTests.TestInterfaces;

            [Nexus<ISimpleClientNexus, IVersionedServerNexusV1_1>(NexusType = NexusType.Client)]
            public partial class VersionedClientNexusV1_1 { }
            """,
    };

    /// <summary>
    /// The versioned nexuses with no server class opted in: a class-less V1 client export
    /// (<c>[assembly: NexusClientExport]</c>) is the only IR export.
    /// </summary>
    public static string[] VersionedV1Export() => new[]
    {
        IrFixtures.Versioned,
        Usings + """
            [assembly: NexusClientExport<NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV1,
                NexNet.IntegrationTests.TestInterfaces.ISimpleClientNexus>(Targets = NexusTargets.Java | NexusTargets.JavaScript)]
            """,
    };

    /// <summary>
    /// Unions, channels (of unions and of primitives), pipes, collections, inherited interfaces, explicit ids and
    /// cancellation, in both directions.
    /// </summary>
    public static string[] Rich() => new[]
    {
        Usings + """
            #nullable enable
            namespace Rich;

            [NexusObject]
            [NexusUnion<Circle>(1)]
            [NexusUnion<Square>(2)]
            public abstract class Shape
            {
                [NexusKey(0)] public string Name { get; set; } = "shape";
            }

            [NexusObject]
            public class Circle : Shape
            {
                [NexusKey(1)] public double Radius { get; set; }
            }

            [NexusObject]
            public class Square : Shape
            {
                [NexusKey(1)] public int Side { get; set; }
            }

            [NexusObject]
            public class Envelope
            {
                [NexusKey(0)] public Shape? Shape { get; set; }
                [NexusKey(1)] public List<string> Tags { get; set; } = new();
                [NexusKey(2)] public Dictionary<string, int> Counts { get; set; } = new();
            }

            public interface IBaseServer
            {
                ValueTask<int> Version();
            }

            public interface IRichServer : IBaseServer
            {
                ValueTask<Shape> Echo(Shape shape, CancellationToken cancellationToken);
                void Fire(Envelope envelope, int? count);
                ValueTask Upload(INexusDuplexPipe pipe);
                ValueTask Shapes(INexusDuplexChannel<Shape> channel);
                ValueTask Numbers(INexusDuplexChannel<long> channel);
                [NexusMethod(50)] ValueTask<string?> Fixed(string? text);
                [NexusMethod(SeparatePool = true)] ValueTask Slow();

                [NexusCollection(NexusCollectionMode.ServerToClient)]
                INexusList<Shape> ShapeList { get; }

                [NexusCollection(NexusCollectionMode.BiDirectional)]
                INexusList<string> Names { get; }
            }

            public interface IBaseClient
            {
                void Notify(string message);
            }

            public interface IRichClient : IBaseClient
            {
                ValueTask<Shape?> Draw(Envelope envelope);
                ValueTask Download(INexusDuplexPipe pipe);
                [NexusMethod(9)] ValueTask Receive(INexusDuplexChannel<Envelope> channel);
                ValueTask<int> Count(CancellationToken cancellationToken);
            }

            [Nexus<IRichServer, IRichClient>(NexusType = NexusType.Server, Targets = NexusTargets.Java | NexusTargets.JavaScript)]
            public partial class RichServerNexus { }

            [Nexus<IRichClient, IRichServer>(NexusType = NexusType.Client)]
            public partial class RichClientNexus { }
            """,
    };
}
