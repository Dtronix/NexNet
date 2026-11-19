// Records invocations.jsonl in the parent folder from the C# implementation: Invocation, InvocationResult and
// MessageStart bodies serialized by the C# message classes, and remote error details written by RemoteErrorDetail.
// The assembly is named NexNet.Fuzz to reach the internal message classes (InternalsVisibleTo).
// Regenerate (from the repository root):
//   dotnet run --project clients/js/packages/nexnet/test/invocation/fixtures/recorder -c Release -- clients/js/packages/nexnet/test/invocation/fixtures
using System.Buffers;
using System.Text.Json;
using NexNet.Messages;
using NexNet.Serialization;

namespace InvocationRecorder;

public static class Program
{
    delegate void Pack(ref MsgPackWriter writer);

    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    static byte[] Bytes(Pack pack)
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buf);
        pack(ref w);
        w.Flush();
        return buf.WrittenSpan.ToArray();
    }

    static string Body<T>(T m) where T : IMessageBase
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buf);
        m.Serialize(ref w);
        w.Flush();
        return Hex(buf.WrittenSpan);
    }

    public static void Main(string[] args)
    {
        var outDir = args[0];
        Directory.CreateDirectory(outDir);
        var opts = new JsonSerializerOptions { WriteIndented = false };
        var lines = new List<string>();
        void Add(string name, MessageType type, object fields, string body)
            => lines.Add(JsonSerializer.Serialize(new { name, type = (int)type, fields, body }, opts));

        // ---------------- Invocation ----------------
        var invocations = new (string Name, ushort Id, ushort Method, InvocationFlags Flags, byte[] Args)[]
        {
            ("ignore-return-no-args", 0, 0, InvocationFlags.IgnoreReturn, Array.Empty<byte>()),
            ("result-int-string", 1, 5, InvocationFlags.None, Bytes((ref MsgPackWriter w) => { w.WriteArrayHeader(2); w.Write(42); w.Write("hi"); })),
            ("pipe", 300, 65535, InvocationFlags.IgnoreReturn | InvocationFlags.DuplexPipe, Bytes((ref MsgPackWriter w) => { w.WriteArrayHeader(1); w.Write((byte)7); })),
            ("mixed", 65535, 128, InvocationFlags.None, Bytes((ref MsgPackWriter w) => { w.WriteArrayHeader(3); w.Write(-1); w.WriteNil(); w.Write(true); })),
            ("nested", 2, 1, InvocationFlags.None, Bytes((ref MsgPackWriter w) => { w.WriteArrayHeader(1); w.WriteArrayHeader(2); w.WriteMapHeader(1); w.Write("k"); w.Write(1L << 40); w.WriteBinary(new byte[] { 1, 2, 3 }); })),
        };
        foreach (var (name, id, method, flags, a) in invocations)
        {
            var m = new InvocationMessage { InvocationId = id, MethodId = method, Flags = flags };
            m.SetArguments(a);
            Add("Invocation/" + name, MessageType.Invocation,
                new { invocationId = (int)id, methodId = (int)method, flags = (int)flags, args = Hex(a.Length == 0 ? new byte[] { 0x90 } : a) }, Body(m));
        }

        // ---------------- InvocationResult ----------------
        var results = new (string Name, ushort Id, InvocationResultMessage.StateType State, byte[]? Value)[]
        {
            ("completed-no-value", 1, InvocationResultMessage.StateType.CompletedResult, null),
            ("completed-int", 2, InvocationResultMessage.StateType.CompletedResult, Bytes((ref MsgPackWriter w) => w.Write(42))),
            ("completed-nil", 3, InvocationResultMessage.StateType.CompletedResult, Bytes((ref MsgPackWriter w) => w.WriteNil())),
            ("completed-string", 300, InvocationResultMessage.StateType.CompletedResult, Bytes((ref MsgPackWriter w) => w.Write(new string('x', 40)))),
            ("exception-no-detail", 5, InvocationResultMessage.StateType.Exception, null),
            ("exception-type-message", 6, InvocationResultMessage.StateType.Exception, Bytes((ref MsgPackWriter w) => RemoteErrorDetail.Write(ref w, null, "System.InvalidOperationException", "boom"))),
            ("exception-app-error", 7, InvocationResultMessage.StateType.Exception, Bytes((ref MsgPackWriter w) => RemoteErrorDetail.Write(ref w, 17, null, "app error"))),
            ("exception-negative-code", 65535, InvocationResultMessage.StateType.Exception, Bytes((ref MsgPackWriter w) => RemoteErrorDetail.Write(ref w, int.MinValue, null, null))),
            ("unauthorized", 8, InvocationResultMessage.StateType.Unauthorized, null),
            ("canceled", 9, InvocationResultMessage.StateType.Canceled, null),
        };
        foreach (var (name, id, state, value) in results)
        {
            var m = new InvocationResultMessage { InvocationId = id, State = state };
            if (value != null)
                m.Result = new ReadOnlySequence<byte>(value);
            Add("InvocationResult/" + name, MessageType.InvocationResult,
                new { invocationId = (int)id, state = (int)state, value = value == null ? null : Hex(value) }, Body(m));
        }

        // ---------------- MessageStart ----------------
        foreach (var (name, id, kind, inner, total) in new (string, ushort, LargeMessageKind, MessageType, uint)[]
                 {
                     ("argument", 1, LargeMessageKind.Argument, MessageType.Invocation, 70000),
                     ("result", 65535, LargeMessageKind.Result, MessageType.InvocationResult, 4 * 1024 * 1024),
                     ("small", 0, LargeMessageKind.Result, MessageType.InvocationResult, 1),
                 })
        {
            var m = new MessageStartMessage { InvocationId = id, Kind = kind, InnerType = inner, TotalLength = total };
            Add("MessageStart/" + name, MessageType.MessageStart,
                new { invocationId = (int)id, kind = (int)kind, innerType = (int)inner, totalLength = total }, Body(m));
        }

        // ---------------- RemoteErrorDetail (truncation) ----------------
        var longType = new string('t', 300);
        // A surrogate pair at code units 2047-2048: truncation to 2048 must drop the whole pair.
        var straddle = new string('m', 2047) + char.ConvertFromUtf32(0x1F600) + "tail";
        var exact = new string('e', 2048);
        var unicodeType = "Ns.Typ" + (char)0xE9;
        var unicodeMessage = (char)0xFC + "ber " + (char)0x4E2D + (char)0x6587;
        foreach (var (name, code, type, message) in new (string, int?, string?, string?)[]
                 {
                     ("all-null", null, null, null),
                     ("truncate-type", 1, longType, "m"),
                     ("truncate-surrogate", null, "T", straddle),
                     ("exact-limit", -5, new string('u', 256), exact),
                     ("unicode", 0, unicodeType, unicodeMessage),
                 })
        {
            var detail = Bytes((ref MsgPackWriter w) => RemoteErrorDetail.Write(ref w, code, type, message));
            Add("RemoteErrorDetail/" + name, MessageType.InvocationResult, new { code, type, message }, Hex(detail));
        }

        File.WriteAllLines(Path.Combine(outDir, "invocations.jsonl"), lines);
        Console.WriteLine($"Wrote {lines.Count} vectors to {outDir}");
    }
}
