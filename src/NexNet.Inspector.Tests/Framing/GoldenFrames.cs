using System.Buffers.Binary;
using System.Text.Json;
using NexNet.Inspector.Core.Framing;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Framing;

/// <summary>A golden frame and what the parser must report for it, derived from the vector's value, not its bytes.</summary>
internal sealed record GoldenFrame(
    string Id,
    byte[] Bytes,
    FrameKind Kind,
    MessageType Type,
    int HeaderLength,
    int PostHeaderLength,
    int BodyLength,
    FrameTarget Target,
    ushort TargetId,
    byte ChunkKind);

/// <summary>Loads tests/golden/frames.jsonl and messages.jsonl (format: tests/golden/README.md).</summary>
internal static class GoldenFrames
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    /// <summary>The preamble vector.</summary>
    public static GoldenFrame Preamble => LoadFrames().Single(f => f.Kind == FrameKind.Preamble);

    /// <summary>Every frame vector except the preamble, in file order.</summary>
    public static IReadOnlyList<GoldenFrame> Frames => LoadFrames().Where(f => f.Kind == FrameKind.Frame).ToList();

    /// <summary>Every message body vector of a framed message type, wrapped in its frame (type, uint16 LE length, body).</summary>
    public static IReadOnlyList<GoldenFrame> Messages => LoadMessages();

    /// <summary>
    /// The preamble, then every frame vector, then every framed message vector, as one client-to-server stream, with the
    /// expected frames in stream order.
    /// </summary>
    public static (byte[] Stream, IReadOnlyList<GoldenFrame> Frames) Stream()
    {
        var frames = new List<GoldenFrame> { Preamble };
        frames.AddRange(Frames);
        frames.AddRange(Messages);
        return (frames.SelectMany(f => f.Bytes).ToArray(), frames);
    }

    private static List<GoldenFrame> LoadFrames()
    {
        var result = new List<GoldenFrame>();
        foreach (var line in ReadLines("frames.jsonl"))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString()!;
            var bytes = Convert.FromHexString(root.GetProperty("hex").GetString()!);
            var value = root.GetProperty("value");

            if (root.GetProperty("type").GetString() == "preamble")
            {
                result.Add(new GoldenFrame(id, bytes, FrameKind.Preamble, MessageType.Unset, 8, 0, 0, FrameTarget.None, 0, 0));
                continue;
            }

            var type = (byte)value[0].GetInt32();
            if (value.GetArrayLength() == 1)
            {
                result.Add(new GoldenFrame(id, bytes, FrameKind.Frame, (MessageType)type, 1, 0, 0, FrameTarget.None, 0, 0));
                continue;
            }

            var frame = (MessageType)type switch
            {
                MessageType.DuplexPipeWrite => new GoldenFrame(id, bytes, FrameKind.Frame, MessageType.DuplexPipeWrite, 3, 2,
                    Convert.FromBase64String(value[3].GetString()!).Length, FrameTarget.Pipe, PipeId(value), 0),
                MessageType.PipeWindowUpdate => new GoldenFrame(id, bytes, FrameKind.Frame, MessageType.PipeWindowUpdate, 3, 2,
                    4, FrameTarget.Pipe, PipeId(value), 0),
                MessageType.MessageChunk => new GoldenFrame(id, bytes, FrameKind.Frame, MessageType.MessageChunk, 3, 3,
                    Convert.FromBase64String(value[3].GetString()!).Length, FrameTarget.Invocation,
                    (ushort)value[1].GetInt32(), (byte)value[2].GetInt32()),
                // A MessagePack body: the vector's value is typed JSON, so its length is what follows the 3-byte header.
                _ => new GoldenFrame(id, bytes, FrameKind.Frame, (MessageType)type, 3, 0, bytes.Length - 3, FrameTarget.None, 0, 0),
            };
            result.Add(frame);
        }

        return result;
    }

    private static List<GoldenFrame> LoadMessages()
    {
        // schemas.jsonl maps a message body's type name to its frame type; null marks a body that is never framed.
        var frameTypes = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var line in ReadLines("schemas.jsonl"))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("kind").GetString() == "message"
                && root.GetProperty("messageType").ValueKind == JsonValueKind.Number)
                frameTypes[root.GetProperty("id").GetString()!] = (byte)root.GetProperty("messageType").GetInt32();
        }

        var result = new List<GoldenFrame>();
        foreach (var line in ReadLines("messages.jsonl"))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!frameTypes.TryGetValue(root.GetProperty("type").GetString()!, out var type))
                continue;

            var body = Convert.FromHexString(root.GetProperty("hex").GetString()!);
            var bytes = new byte[3 + body.Length];
            bytes[0] = type;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), checked((ushort)body.Length));
            body.CopyTo(bytes.AsSpan(3));
            result.Add(new GoldenFrame(root.GetProperty("id").GetString()!, bytes, FrameKind.Frame, (MessageType)type, 3, 0,
                body.Length, FrameTarget.None, 0, 0));
        }

        return result;
    }

    private static ushort PipeId(JsonElement value) => (ushort)(value[1].GetInt32() | (value[2].GetInt32() << 8));

    private static IEnumerable<string> ReadLines(string fileName)
        => File.ReadAllLines(Path.Combine(RepositoryRoot.Value, "tests", "golden", fileName))
            .Where(l => l.Length > 0);

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "NexNet.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (src/NexNet.slnx) not found above " + AppContext.BaseDirectory);
    }
}
