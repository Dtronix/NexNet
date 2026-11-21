namespace NexNet.Generator.Tests.Targets.JavaScript;

/// <summary>Sources for the JavaScript proxy and client nexus emitter tests (J04).</summary>
internal static class JsProxyFixtures
{
    /// <summary>
    /// Every method shape the proxy and client nexus emitters handle: each return kind, cancellation tokens, pipes and
    /// channels, nullable and composed argument types, a user formatter, collections of each mode (and of primitive
    /// items C# writes as ext 78 or bin), separate-pool methods, and names that clash with the runtime base classes or
    /// the generated code.
    /// </summary>
    public const string Chat = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using NexNet;
        using NexNet.Collections;
        using NexNet.Collections.Lists;
        using NexNet.Pipes;
        using NexNet.Serialization;

        [assembly: NexusFormatter<Chat.MoneyFormatter, Chat.Money>]
        [assembly: NexusTargetFormatter<Chat.Money>(NexusTargets.JavaScript, "./ext/money-formatter.js#MoneyFormatter")]
        [assembly: NexusClientExport<Chat.IChatServer, Chat.IChatClient>(Targets = NexusTargets.JavaScript, Name = "ChatServerNexus")]
        [assembly: NexusSerializable<ValueTuple<string>>]
        [assembly: NexusSerializable<ValueTuple<int, int>>]
        [assembly: NexusSerializable<ValueTuple<int?, string>>]
        [assembly: NexusSerializable<ValueTuple<byte, string>>]
        [assembly: NexusSerializable<ValueTuple<byte>>]
        [assembly: NexusSerializable<ValueTuple<int>>]
        [assembly: NexusSerializable<ValueTuple<string, Chat.Money>>]
        [assembly: NexusSerializable<ValueTuple<int, byte>>]
        [assembly: NexusSerializable<ValueTuple<Chat.Box<string>, Chat.Item>>]
        [assembly: NexusSerializable<ValueTuple<int, int, int>>]
        [assembly: NexusSerializable<ValueTuple<Chat.Color[], Dictionary<int, string>>>]
        [assembly: NexusSerializable<ValueTuple<Chat.Color>>]
        [assembly: NexusSerializable<ValueTuple<Chat.Box<int>, Dictionary<string, Chat.Item>, Chat.Color, byte, Chat.Money, Chat.Item[]>>]
        [assembly: NexusSerializable<ValueTuple<List<Chat.Item>>>]
        [assembly: NexusSerializable<ValueTuple<long>>]

        namespace Chat;

        public readonly record struct Money(long Cents);

        public sealed class MoneyFormatter : NexusFormatter<Money>
        {
            public override void Serialize(ref MsgPackWriter writer, Money value) => writer.Write(value.Cents);
            public override void Deserialize(ref MsgPackReader reader, ref Money value) => value = new Money(reader.ReadInt64());
        }

        [NexusObject]
        public class Item
        {
            [NexusKey(0)] public int Id { get; set; }
            [NexusKey(1)] public string? Name { get; set; }
        }

        [NexusObject]
        public class Box<T>
        {
            [NexusKey(0)] public T Value { get; set; } = default!;
        }

        public enum Color : byte { Red, Green }

        /// <summary>The chat server API &amp; its <c>docs</c>.</summary>
        public interface IChatServer
        {
            /// <summary>Sends a message and forgets it.</summary>
            void Notify(string text);
            ValueTask Ping();
            /// <summary>Returns a + b.</summary>
            ValueTask<int> Add(int a, int b, CancellationToken cancellationToken);
            ValueTask<string?> Find(int? id, string? name);
            ValueTask<List<Item>> Items(Box<int> box, Dictionary<string, Item?>? map, Color color, byte small, Money money, Item[] array);
            ValueTask Upload(INexusDuplexPipe pipe, string name);
            ValueTask Stream(INexusDuplexChannel<Item> channel);
            ValueTask<Item?> Invoke(int w, int pipeId);
            ValueTask Constructor();
            ValueTask<long> Wait(CancellationToken token);
            ValueTask<int> Search(string options, CancellationToken cancellationToken);

            /// <summary>The names in the room.</summary>
            [NexusCollection(NexusCollectionMode.BiDirectional)]
            INexusList<string> Names { get; }

            [NexusCollection(NexusCollectionMode.ServerToClient)]
            INexusList<Item> List { get; }

            [NexusCollection(NexusCollectionMode.Relay)]
            INexusList<Box<int>> Boxes { get; }

            [NexusCollection(NexusCollectionMode.ServerToClient)]
            INexusList<int> Scores { get; }

            [NexusCollection(NexusCollectionMode.BiDirectional)]
            INexusList<byte> Bytes { get; }
        }

        public interface IChatClient
        {
            /// <summary>Returns the square of the value.</summary>
            ValueTask<int> Square(int value, CancellationToken cancellationToken);
            void Message(string text, Money money);
            ValueTask Receive(INexusDuplexPipe pipe);
            ValueTask Feed(int count, INexusDuplexChannel<Item> channel);
            [NexusMethod(SeparatePool = true)]
            ValueTask Heavy();
            [NexusMethod(SeparatePool = true)]
            ValueTask<List<Item>?> Context(Box<string> box, Item? item);
            ValueTask OnConnected(int arguments, int context, int methodId);
            ValueTask<Color> Pick(Color[] colors, Dictionary<int, string>? map);
        }

        /// <summary>Invocation arguments and results as the C# serializer writes them, for the Node smoke runner.</summary>
        public static class ArgumentSamples
        {
            public static Dictionary<string, byte[]> All() => new()
            {
                ["NOTIFY"] = NexusSerializer.Serialize(ValueTuple.Create("hi")),
                ["ADD"] = NexusSerializer.Serialize((1, 2)),
                ["THREE"] = NexusSerializer.Serialize(ValueTuple.Create(3)).AsSpan(1).ToArray(),
                ["FIND"] = NexusSerializer.Serialize(((int?)null, (string?)"x")),
                ["ITEMS"] = NexusSerializer.Serialize((new Box<int> { Value = 5 }, (Dictionary<string, Item?>?)new Dictionary<string, Item?> { ["k"] = null },
                    Color.Green, (byte)200, new Money(42), new[] { new Item { Id = 1, Name = "a" } })),
                ["ITEMS_RESULT"] = NexusSerializer.Serialize(ValueTuple.Create(new List<Item> { new Item { Id = 2, Name = "b" } })).AsSpan(1).ToArray(),
                ["UPLOAD"] = NexusSerializer.Serialize(((byte)7, "n")),
                ["STREAM"] = NexusSerializer.Serialize(ValueTuple.Create((byte)7)),
                ["INVOKE"] = NexusSerializer.Serialize((3, 4)),
                ["WAIT_RESULT"] = NexusSerializer.Serialize(ValueTuple.Create(9000000000L)).AsSpan(1).ToArray(),
                ["SEARCH"] = NexusSerializer.Serialize(ValueTuple.Create("q")),
                ["SQUARE"] = NexusSerializer.Serialize(ValueTuple.Create(5)),
                ["SQUARED"] = NexusSerializer.Serialize(ValueTuple.Create(25)).AsSpan(1).ToArray(),
                ["MESSAGE"] = NexusSerializer.Serialize(("t", new Money(3))),
                ["RECEIVE"] = NexusSerializer.Serialize(ValueTuple.Create((byte)9)),
                ["FEED"] = NexusSerializer.Serialize((2, (byte)8)),
                ["CONTEXT"] = NexusSerializer.Serialize((new Box<string> { Value = "b" }, (Item?)new Item { Id = 1, Name = "n" })),
                ["CONTEXT_RESULT"] = NexusSerializer.Serialize(ValueTuple.Create((List<Item>?)null)).AsSpan(1).ToArray(),
                ["ON_CONNECTED"] = NexusSerializer.Serialize((1, 2, 3)),
                ["PICK"] = NexusSerializer.Serialize((new[] { Color.Green }, (Dictionary<int, string>?)new Dictionary<int, string> { [1] = "x" })),
                ["GREEN"] = NexusSerializer.Serialize(ValueTuple.Create(Color.Green)).AsSpan(1).ToArray(),
            };
        }
        """;
}
