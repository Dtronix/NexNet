// Records collections.jsonl in the parent folder from the C# implementation:
//  - "message" lines: list items (protocol spec 4.11) serialized by NexusCollectionListMessageFormatter, with their fields;
//  - "stream" lines: item streams fed, item by item, to the C# client state machine (NexusListClient.OnProcess, spec 12.4),
//    with what it did after each item: protocol error, list contents, version, raised change events, and the answer
//    to the request pending before the item (true/false, or null while still pending). A "close" step runs the
//    client's OnDisconnected, then records the Reset that NexusBroadcastClient.Disconnected raises after it.
// Every item is serialized, then deserialized again before it is processed, as it would arrive from the pipe.
// The assembly is named NexNet.Fuzz to reach the internal collection classes (InternalsVisibleTo).
// Regenerate (from the repository root):
//   dotnet run --project clients/js/packages/nexnet/test/collections/fixtures/recorder -c Release -- clients/js/packages/nexnet/test/collections/fixtures
using System.Buffers;
using System.Reflection;
using System.Text.Json;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Internals.Threading;
using NexNet.Serialization;

namespace CollectionRecorder;

internal sealed class Probe : NexusListClient<int>
{
    private static readonly FieldInfo ItemListField =
        typeof(NexusListClient<int>).GetField("_itemList", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo CompletionField =
        typeof(NexusListClient<int>).GetField("_operationCompletionSource", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public readonly List<string> Events = new();

    public Probe() : base(1, NexusCollectionMode.BiDirectional, null)
    {
        Changed.Subscribe(args => Events.Add(args.ChangedAction.ToString().ToLowerInvariant()));
    }

    public bool Process(INexusCollectionListMessage message) => OnProcess(message, null, CancellationToken.None).Disconnect;

    public void Close()
    {
        OnDisconnected();
        // NexusBroadcastClient.Disconnected raises Reset after OnDisconnected.
        Events.Add("reset");
    }

    public int Version
    {
        get
        {
            var list = ItemListField.GetValue(this)!;
            return (int)list.GetType().GetProperty("Version")!.GetValue(list)!;
        }
    }

    private PooledResettableValueTaskCompletionSource<bool> Completion =>
        (PooledResettableValueTaskCompletionSource<bool>)CompletionField.GetValue(this)!;

    /// <summary>A request is pending from here on (the completion source is reset, as ProcessMessage does).</summary>
    public void BeginRequest() => Completion.Reset();

    /// <summary>The answer to the pending request, or null while it is pending.</summary>
    public bool? Answer()
    {
        var task = Completion.Task;
        return task.IsCompleted ? task.Result : null;
    }
}

public static class Program
{
    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    static byte[] Serialize(INexusCollectionListMessage message)
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buf);
        NexusCollectionListMessageFormatter.Instance.Serialize(ref w, message);
        w.Flush();
        return buf.WrittenSpan.ToArray();
    }

    static INexusCollectionListMessage Deserialize(byte[] bytes)
    {
        var r = new MsgPackReader(new ReadOnlyMemory<byte>(bytes));
        INexusCollectionListMessage? message = null;
        NexusCollectionListMessageFormatter.Instance.Deserialize(ref r, ref message);
        return message!;
    }

    static NexusCollectionMessageFlags F(bool ack) => ack ? NexusCollectionMessageFlags.Ack : NexusCollectionMessageFlags.Unset;

    static INexusCollectionListMessage Start(int version, int total)
    {
        var m = NexusCollectionListResetStartMessage.Rent();
        m.Version = version; m.TotalValues = total;
        return m;
    }

    static INexusCollectionListMessage Values<T>(T[]? values)
    {
        var m = NexusCollectionListResetValuesMessage.Rent();
        m.Values = values == null ? Memory<byte>.Empty : PayloadSerializer.Serialize(values);
        return m;
    }

    static INexusCollectionListMessage Values(params int[] values) => Values<int>(values);

    static INexusCollectionListMessage Complete() => NexusCollectionListResetCompleteMessage.Rent();

    static INexusCollectionListMessage Clear(int version, bool ack = false)
    {
        var m = NexusCollectionListClearMessage.Rent();
        m.Flags = F(ack); m.Version = version;
        return m;
    }

    static INexusCollectionListMessage Insert<T>(int version, int index, T value, bool ack = false)
    {
        var m = NexusCollectionListInsertMessage.Rent();
        m.Flags = F(ack); m.Version = version; m.Index = index; m.Value = PayloadSerializer.Serialize(value);
        return m;
    }

    static INexusCollectionListMessage Replace<T>(int version, int index, T value, bool ack = false)
    {
        var m = NexusCollectionListReplaceMessage.Rent();
        m.Flags = F(ack); m.Version = version; m.Index = index; m.Value = PayloadSerializer.Serialize(value);
        return m;
    }

    static INexusCollectionListMessage Move(int version, int from, int to, bool ack = false)
    {
        var m = NexusCollectionListMoveMessage.Rent();
        m.Flags = F(ack); m.Version = version; m.FromIndex = from; m.ToIndex = to;
        return m;
    }

    static INexusCollectionListMessage Remove(int version, int index, bool ack = false)
    {
        var m = NexusCollectionListRemoveMessage.Rent();
        m.Flags = F(ack); m.Version = version; m.Index = index;
        return m;
    }

    static INexusCollectionListMessage Noop(bool ack = true)
    {
        var m = NexusCollectionListNoopMessage.Rent();
        m.Flags = F(ack);
        return m;
    }

    /// <summary>Marks the pipe closing (OnDisconnected) in a stream.</summary>
    static readonly object CloseStep = new();

    public static void Main(string[] args)
    {
        var outDir = args[0];
        Directory.CreateDirectory(outDir);
        var opts = new JsonSerializerOptions { WriteIndented = false };
        var lines = new List<string>();
        var uber = (char)0xFC + "ber";

        // ---------------- Messages ----------------
        void Msg(string name, object fields, INexusCollectionListMessage message)
            => lines.Add(JsonSerializer.Serialize(new { kind = "message", name, fields, hex = Hex(Serialize(message)) }, opts));

        Msg("ResetStart/v0", new { tag = 0, flags = 0, version = 0, totalValues = 0 }, Start(0, 0));
        Msg("ResetStart/large", new { tag = 0, flags = 0, version = 70000, totalValues = 1000 }, Start(70000, 1000));
        Msg("ResetValues/int", new { tag = 1, flags = 0, values = new[] { 1, -1, 300, int.MaxValue } }, Values(1, -1, 300, int.MaxValue));
        Msg("ResetValues/empty", new { tag = 1, flags = 0, values = Array.Empty<int>() }, Values());
        Msg("ResetValues/nil", new { tag = 1, flags = 0, values = (int[]?)null }, Values<int>(null));
        Msg("ResetValues/string", new { tag = 1, flags = 0, strings = new[] { "a", null, uber } }, Values(new[] { "a", null, uber }));
        Msg("ResetComplete", new { tag = 2, flags = 0 }, Complete());
        Msg("Clear/request", new { tag = 3, flags = 0, version = 5 }, Clear(5));
        Msg("Clear/ack", new { tag = 3, flags = 1, version = 6 }, Clear(6, true));
        Msg("Insert/request", new { tag = 4, flags = 0, version = 4, index = 2, value = 42 }, Insert(4, 2, 42));
        Msg("Insert/ack", new { tag = 4, flags = 1, version = 5, index = 2, value = 42 }, Insert(5, 2, 42, true));
        Msg("Insert/string", new { tag = 4, flags = 0, version = 1, index = 0, @string = "hi" }, Insert(1, 0, "hi"));
        Msg("Insert/string-null", new { tag = 4, flags = 0, version = 1, index = 0, @string = (string?)null }, Insert<string?>(1, 0, null));
        Msg("Replace/request", new { tag = 5, flags = 0, version = 9, index = 0, value = -7 }, Replace(9, 0, -7));
        Msg("Replace/ack", new { tag = 5, flags = 1, version = 300, index = 65536, value = 1 }, Replace(300, 65536, 1, true));
        Msg("Move/request", new { tag = 6, flags = 0, version = 3, fromIndex = 0, toIndex = 2 }, Move(3, 0, 2));
        Msg("Move/ack", new { tag = 6, flags = 1, version = 4, fromIndex = 2, toIndex = 0 }, Move(4, 2, 0, true));
        Msg("Remove/request", new { tag = 7, flags = 0, version = 2, index = 1 }, Remove(2, 1));
        Msg("Remove/ack", new { tag = 7, flags = 1, version = 3, index = 1 }, Remove(3, 1, true));
        Msg("Noop/ack", new { tag = 8, flags = 1 }, Noop());
        Msg("Noop/plain", new { tag = 8, flags = 0 }, Noop(false));

        // ---------------- Streams ----------------
        void Stream(string name, params object[] steps)
        {
            var probe = new Probe();
            var recorded = new List<object>();
            foreach (var step in steps)
            {
                var before = probe.Events.Count;
                // A request is pending before every item; one still unanswered stays pending.
                if (probe.Answer() != null)
                    probe.BeginRequest();
                if (ReferenceEquals(step, CloseStep))
                {
                    probe.Close();
                    recorded.Add(new
                    {
                        close = true,
                        list = probe.ToArray(),
                        version = probe.Version,
                        events = probe.Events.Skip(before).ToArray(),
                        answer = probe.Answer(),
                    });
                    continue;
                }

                var bytes = Serialize((INexusCollectionListMessage)step);
                var disconnect = probe.Process(Deserialize(bytes));
                recorded.Add(new
                {
                    hex = Hex(bytes),
                    disconnect,
                    list = probe.ToArray(),
                    version = probe.Version,
                    events = probe.Events.Skip(before).ToArray(),
                    answer = probe.Answer(),
                });
            }

            lines.Add(JsonSerializer.Serialize(new { kind = "stream", name, steps = recorded }, opts));
        }

        Stream("reset/basic", Start(7, 3), Values(1, 2), Values(3), Complete());
        Stream("reset/empty", Start(0, 0), Complete());
        Stream("reset/nil-values", Start(2, 0), Values<int>(null), Complete());
        // Protocol spec 12.10: a change already in the snapshot (version 7) and one after it (version 8) during the reset.
        Stream("reset/interleaved", Start(7, 5), Values(1, 2, 3), Insert(7, 0, 99), Insert(8, 5, 100), Values(4, 5), Complete());
        // F17: changes before ResetStart are buffered and replayed with the stale rule.
        Stream("reset/changes-before-start", Insert(3, 0, 10), Insert(2, 0, 11), Start(2, 1), Values(5), Complete(), Insert(4, 2, 12));
        Stream("reset/buffered-invalid", Insert(5, 3, 10), Start(4, 1), Values(5), Complete());
        Stream("reset/buffered-noop", Noop(), Start(1, 0), Complete());
        Stream("reset/second-start-in-ready", Start(1, 1), Values(1), Complete(), Start(5, 2), Values(7, 8), Complete());

        Stream("ready/each-change",
            Start(1, 3), Values(1, 2, 3), Complete(),
            Insert(2, 1, 9), Replace(3, 0, 8), Move(4, 0, 3), Move(5, 3, 1), Remove(6, 2), Insert(7, 3, 6), Clear(8), Insert(9, 0, 1));
        Stream("ready/ack-answers",
            Start(0, 0), Complete(),
            Insert(1, 0, 5, true), Insert(2, 1, 6), Replace(3, 1, 7, true), Move(4, 0, 1, true), Remove(5, 0, true), Clear(6, true));
        Stream("ready/noop", Start(0, 1), Values(4), Complete(), Noop(), Noop(false), Insert(1, 0, 3, true));
        Stream("ready/stale-dropped", Start(5, 1), Values(4), Complete(), Insert(5, 0, 1, true), Insert(3, 0, 2), Clear(4), Insert(6, 0, 3));
        Stream("ready/version-zero", Start(0, 1), Values(4), Complete(), Insert(0, 0, 1), Insert(0, 0, 2), Remove(0, 2), Clear(0, true));
        Stream("ready/gaps", Start(1, 0), Complete(), Insert(5, 0, 1), Insert(9, 1, 2));

        Stream("error/insert-out-of-range", Start(0, 1), Values(1), Complete(), Insert(1, 2, 5, true));
        Stream("error/insert-negative", Start(0, 1), Values(1), Complete(), Insert(1, -1, 5));
        Stream("error/remove-out-of-range", Start(0, 1), Values(1), Complete(), Remove(1, 1, true));
        Stream("error/replace-out-of-range", Start(0, 0), Complete(), Replace(1, 0, 1));
        Stream("error/move-same-index", Start(0, 2), Values(1, 2), Complete(), Move(1, 1, 1, true));
        Stream("error/move-out-of-range", Start(0, 2), Values(1, 2), Complete(), Move(1, 0, 2));
        Stream("error/values-in-ready", Start(0, 0), Complete(), Values(1));
        Stream("error/complete-in-ready", Start(0, 0), Complete(), Complete());
        Stream("error/values-before-start", Values(1));
        Stream("error/start-while-resetting", Start(0, 1), Start(0, 1));
        Stream("error/negative-total", Start(0, -1));
        Stream("error/too-many-values", Start(0, 2), Values(1), Values(2, 3));
        Stream("error/too-few-values", Start(0, 2), Values(1), Complete());

        Stream("close/after-ready", Start(3, 2), Values(1, 2), Complete(), CloseStep);
        Stream("close/during-reset", Insert(1, 0, 1), Start(0, 2), Values(1), CloseStep, Start(4, 1), Values(9), Complete());
        Stream("close/pending-request", Start(0, 0), Complete(), CloseStep);

        File.WriteAllLines(Path.Combine(outDir, "collections.jsonl"), lines);
        Console.WriteLine($"Wrote {lines.Count} vectors to {outDir}");
    }
}
