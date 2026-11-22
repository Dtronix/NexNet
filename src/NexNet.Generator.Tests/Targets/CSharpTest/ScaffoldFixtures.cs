using NexNet.Generator.Tests.Ir;

namespace NexNet.Generator.Tests.Targets.CSharpTest;

/// <summary>Sources of the test-scaffolding tests.</summary>
internal static class ScaffoldFixtures
{
    /// <summary>The integration tests' IServerNexus/IClientNexus interfaces (no nexus classes).</summary>
    public static readonly string BasicInterfaces = Cut(IrFixtures.Basic, "[Nexus<");

    /// <summary>The integration tests' versioned interfaces (no nexus classes).</summary>
    public static readonly string VersionedInterfaces = Cut(IrFixtures.Versioned, "[Nexus<");

    public const string BasicAttribute = """
        [assembly: NexNet.Interop.NexusTestScaffolding<NexNet.IntegrationTests.TestInterfaces.IServerNexus, NexNet.IntegrationTests.TestInterfaces.IClientNexus>]
        """;

    public const string VersionedAttribute = """
        [assembly: NexNet.Interop.NexusTestScaffolding<NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV2, NexNet.IntegrationTests.TestInterfaces.ISimpleClientNexus>]
        [assembly: NexNet.Interop.NexusTestScaffolding<NexNet.IntegrationTests.TestInterfaces.INonVersionedServerNexus, NexNet.IntegrationTests.TestInterfaces.ISimpleClientNexus>]
        """;

    /// <summary>
    /// A loadable nexus pair over unions, generics, enums, maps, big integers, pipes, channels and a collection. The
    /// client nexus class makes the generator emit (and register) the formatters of every type both interfaces use.
    /// </summary>
    public const string Runtime = ScaffoldTestRunner.Usings + """
        [assembly: NexusTestScaffolding<Rt.IRtServer, Rt.IRtClient>]

        namespace Rt;

        public enum Color : byte
        {
            Red = 1,
            Blue = 4,
        }

        [NexusObject]
        [NexusUnion<Circle>(1)]
        [NexusUnion<Square>(2)]
        public abstract class Shape
        {
            [NexusKey(0)] public string Name { get; set; } = "";
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
        public class Envelope<T>
        {
            [NexusKey(0)] public T Value { get; set; } = default!;
            [NexusKey(2)] public List<T>? History { get; set; }
        }

        public interface IRtServer
        {
            void Fire(Shape? shape, int? count);
            ValueTask<Envelope<int>> Wrap(int value);
            ValueTask<Dictionary<Color, List<Shape>>> Lookup(Color color, CancellationToken cancellationToken);
            ValueTask Upload(INexusDuplexPipe pipe);
            ValueTask Stream(INexusDuplexChannel<Shape> channel, CancellationToken cancellationToken);
            ValueTask<long> Big(ulong value, decimal price, DateTimeOffset when, int[] ints);
            ValueTask<string?> Maybe(string? text = null, int count = -2);

            [NexusCollection(NexusCollectionMode.ServerToClient)]
            INexusList<Shape> Shapes { get; }
        }

        public interface IRtClient
        {
            ValueTask<int> Ping(int value, string? note);
            void Notify(Color color);
            ValueTask Receive(INexusDuplexPipe pipe);
        }

        [Nexus<IRtClient, IRtServer>(NexusType = NexusType.Client)]
        public partial class RtClientNexus
        {
            public ValueTask<int> Ping(int value, string? note) => new(value);
            public void Notify(Color color) { }
            public ValueTask Receive(INexusDuplexPipe pipe) => default;
        }
        """;

    /// <summary>Drives the generated classes of <see cref="Runtime"/> with a fake invoker and returns a transcript.</summary>
    public const string RuntimeProbe = ScaffoldTestRunner.Usings + """
        using System.Linq;
        using System.Text.Json.Nodes;

        namespace Rt;

        public sealed class FakeInvoker : IRemoteInvoker
        {
            public readonly List<string> Log = new();
            public JsonNode? Result;

            private void Record(RemoteInvocation i)
                => Log.Add(i.Method.Name + "#" + i.Method.Id + " " + i.Arguments.ToJsonString() + " cancel=" + i.Cancel + " pipe=" + (i.Pipe != null));

            public void Invoke(RemoteInvocation invocation) => Record(invocation);

            public ValueTask InvokeAsync(RemoteInvocation invocation)
            {
                Record(invocation);
                return default;
            }

            public ValueTask<JsonNode?> InvokeWithResultAsync(RemoteInvocation invocation)
            {
                Record(invocation);
                return new(Result?.DeepClone());
            }

            public IRemoteList<T> GetList<T>(RemoteCollection collection, TypedCodec<T> codec)
            {
                Log.Add("list " + collection + " #" + collection.Id + " " + collection.Mode + " " + codec.Schema);
                return null!;
            }
        }

        public static class Probe
        {
            public static async Task<string> Run()
            {
                var invoker = new FakeInvoker();
                var log = invoker.Log;
                IRtServer asInterface = Remote_IRtServer.Create(invoker);
                var proxy = (Remote_IRtServer)asInterface;

                proxy.Fire(new Circle { Name = "c", Radius = 2.5 }, null);
                asInterface.Fire(null, 3);

                invoker.Result = JsonNode.Parse("[5, null, [1, 2]]");
                var envelope = await proxy.Wrap(7);
                log.Add("Wrap -> " + envelope.Value + " [" + string.Join(",", envelope.History!) + "]");

                invoker.Result = JsonNode.Parse("[[4, [[2, [\"sq\", 3]]]]]");
                var lookup = await proxy.Lookup(Color.Blue, RemoteCancel.After(250));
                var square = (Square)lookup[Color.Blue][0];
                log.Add("Lookup -> " + square.Name + " " + square.Side);

                using (var cts = new CancellationTokenSource())
                {
                    invoker.Result = JsonNode.Parse("[]");
                    await asInterface.Lookup(Color.Red, cts.Token);
                    await asInterface.Lookup(Color.Red, CancellationToken.None);
                }

                await proxy.Upload((IRemotePipe)null!);
                await proxy.Stream((IRemotePipe)null!, RemoteCancel.After(5));
                try
                {
                    await asInterface.Upload(null!);
                }
                catch (NotSupportedException e)
                {
                    log.Add("Upload(INexusDuplexPipe) -> " + e.Message);
                }

                invoker.Result = JsonValue.Create("-9007199254740993");
                var big = await proxy.Big(ulong.MaxValue, 1.25m, new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)), new[] { 1, -1 });
                log.Add("Big -> " + big);

                invoker.Result = null;
                log.Add("Maybe -> " + (await proxy.Maybe() ?? "null"));

                _ = proxy.Shapes;
                _ = proxy.Shapes;
                try
                {
                    _ = asInterface.Shapes;
                }
                catch (NotSupportedException e)
                {
                    log.Add("INexusList -> " + e.Message);
                }

                log.Add("methods: " + string.Join(", ", new[] { Remote_IRtServer.RemoteMethods.Fire, Remote_IRtServer.RemoteMethods.Stream, Remote_IRtServer.RemoteMethods.Big }
                    .Select(m => m + "#" + m.Id + " " + m.ReturnKind + " " + m.PipeKind + " ct=" + m.HasCancellation + " (" + string.Join(",", m.ParameterNames) + ")")));

                // Script facade.
                var script = new RtClientScript();
                log.Add("slots: " + string.Join(", ", script.Slots.Select(s => s.Name + (s.Method == null ? "" : "#" + s.Method.Id))));
                script.Ping.Delay(10).Returns(54321).Times(1);
                script.Notify.Throws(ErrorKind.InvalidOperation, "no");
                script.Receive.Named("pipe.echo");
                script.OnConnected.AwaitSignal("go");
                foreach (var slot in script.Slots)
                    log.Add("steps " + slot.Name + ": " + string.Join(" | ", slot.Steps.Select(s => s is ReturnStep r ? "Return " + r.Value?.ToJsonString() : s.ToString())) + " times=" + slot.TimesLimit);

                script.Ping.ReportCall(new JsonArray(5, "hi"));
                var call = await script.Ping.NextCallAsync();
                log.Add("call: " + call);
                script.Notify.ReportCall(new JsonArray(4));
                log.Add("call: " + await script.Notify.NextCallAsync());

                script.Ping.Clear();
                script.Ping.Forward(a => new ValueTask<int>(a.value * 2));
                var forwarded = await script.Ping.ForwardAsync(new JsonArray(21, null), CancellationToken.None);
                log.Add("forward: " + forwarded!.ToJsonString());
                log.Add("args: " + RtClientScript.PingArgs.__Codec.Encode(new RtClientScript.PingArgs(1, null)).ToJsonString());

                // Codecs registered by the module initializers.
                log.Add("codec Shape: " + TypedCodecs.Get<Shape?>().Encode(new Square { Name = "s", Side = -1 })!.ToJsonString());
                log.Add("codec ulong: " + TypedCodecs.Get<ulong>().Encode(ulong.MaxValue)!.ToJsonString());
                log.Add("codec Color: " + TypedCodecs.Get<Color>().Encode(Color.Blue)!.ToJsonString());
                return string.Join("\n", log);
            }
        }
        """;

    private static string Cut(string source, string marker) => source.Substring(0, source.IndexOf(marker, StringComparison.Ordinal));
}
