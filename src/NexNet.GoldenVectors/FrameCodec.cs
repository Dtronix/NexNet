using System.Buffers.Binary;
using System.Text.Json.Nodes;
using NexNet.Interop;

namespace NexNet.GoldenVectors;

/// <summary>
/// The connection preamble and frames of the protocol specification (§1, §2) in the typed-JSON model of the golden
/// format (tests/golden/README.md, "frames.jsonl"):
/// <list type="bullet">
/// <item>no body (Ping, the disconnects 20–38): <c>[type]</c>;</item>
/// <item>a MessagePack body: <c>[type, body]</c>, the body as the typed JSON of the message of that type;</item>
/// <item>DuplexPipeWrite: <c>[50, clientId, serverId, "base64 data"]</c>;</item>
/// <item>PipeWindowUpdate: <c>[51, clientId, serverId, credit]</c>;</item>
/// <item>MessageChunk: <c>[114, invocationId, kind, "base64 data"]</c>;</item>
/// <item>the preamble: <c>[version]</c>.</item>
/// </list>
/// </summary>
public static class FrameCodec
{
    /// <summary>Type of a frame vector.</summary>
    public const string FrameType = "frame";

    /// <summary>Type of a preamble vector.</summary>
    public const string PreambleType = "preamble";

    private const int DuplexPipeWrite = 50;
    private const int PipeWindowUpdate = 51;
    private const int MessageChunk = 114;
    private static readonly byte[] Magic = [0x4E, 0x6E, 0x50, 0x14];

    /// <summary>True for the types without a length or body: Ping and the disconnects.</summary>
    public static bool HasNoBody(int type) => type == 1 || type is >= 20 and <= 38;

    /// <summary>Encodes a preamble value.</summary>
    public static byte[] EncodePreamble(JsonNode? value)
    {
        var array = AsArray(value, 1);
        return [.. Magic, 0, 0, 0, checked((byte)(int)array[0]!)];
    }

    /// <summary>Decodes a preamble.</summary>
    public static JsonNode DecodePreamble(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8 || !data.Slice(0, 4).SequenceEqual(Magic) || data[4] != 0 || data[5] != 0 || data[6] != 0)
            throw new FormatException("Not a NexNet preamble.");
        return new JsonArray((int)data[7]);
    }

    /// <summary>Encodes a frame value.</summary>
    public static byte[] Encode(JsonNode? value, GoldenSchemas schemas)
    {
        var array = value as JsonArray ?? throw new FormatException("A frame must be an array.");
        var type = (int)array[0]!;
        if (HasNoBody(type))
        {
            AsArray(value, 1);
            return [(byte)type];
        }

        switch (type)
        {
            case DuplexPipeWrite:
            {
                AsArray(value, 4);
                var data = Convert.FromBase64String((string)array[3]!);
                return [.. Header(type, data.Length), checked((byte)(int)array[1]!), checked((byte)(int)array[2]!), .. data];
            }
            case PipeWindowUpdate:
            {
                AsArray(value, 4);
                var credit = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(credit, checked((uint)(long)array[3]!));
                return [.. Header(type, 4), checked((byte)(int)array[1]!), checked((byte)(int)array[2]!), .. credit];
            }
            case MessageChunk:
            {
                AsArray(value, 4);
                var data = Convert.FromBase64String((string)array[3]!);
                var id = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(id, checked((ushort)(int)array[1]!));
                return [.. Header(type, data.Length), .. id, checked((byte)(int)array[2]!), .. data];
            }
            default:
            {
                AsArray(value, 2);
                var message = schemas.FindMessage(type) ?? throw new FormatException("No message has frame type " + type + ".");
                var body = TypedJson.ToMsgPack(array[1], TypeSchema.Decl(message.Id), schemas.Set);
                return [.. Header(type, body.Length), .. body];
            }
        }
    }

    /// <summary>Decodes one complete frame.</summary>
    /// <exception cref="FormatException">The bytes are not exactly one valid frame.</exception>
    public static JsonNode Decode(ReadOnlySpan<byte> frame, GoldenSchemas schemas)
    {
        if (frame.Length < 1)
            throw new FormatException("Empty frame.");
        int type = frame[0];
        if (HasNoBody(type))
        {
            if (frame.Length != 1)
                throw new FormatException("Trailing bytes after a frame without a body.");
            return new JsonArray(type);
        }

        if (frame.Length < 3)
            throw new FormatException("Truncated frame header.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(1));
        var postHeader = type switch
        {
            DuplexPipeWrite or PipeWindowUpdate => 2,
            MessageChunk => 3,
            _ => 0,
        };
        if (frame.Length != 3 + postHeader + length)
            throw new FormatException("Frame length " + frame.Length + " does not match its header.");
        var post = frame.Slice(3, postHeader);
        var body = frame.Slice(3 + postHeader);
        switch (type)
        {
            case DuplexPipeWrite:
                return new JsonArray(type, (int)post[0], (int)post[1], Convert.ToBase64String(body));
            case PipeWindowUpdate:
                if (length != 4)
                    throw new FormatException("A PipeWindowUpdate body is 4 bytes.");
                return new JsonArray(type, (int)post[0], (int)post[1], (long)BinaryPrimitives.ReadUInt32LittleEndian(body));
            case MessageChunk:
                return new JsonArray(type, (int)BinaryPrimitives.ReadUInt16LittleEndian(post), (int)post[2], Convert.ToBase64String(body));
            default:
            {
                var message = schemas.FindMessage(type) ?? throw new FormatException("No message has frame type " + type + ".");
                return new JsonArray(type, TypedJson.FromMsgPack(body.ToArray(), TypeSchema.Decl(message.Id), schemas.Set));
            }
        }
    }

    private static byte[] Header(int type, int bodyLength)
    {
        var header = new byte[3];
        header[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), checked((ushort)bodyLength));
        return header;
    }

    private static JsonArray AsArray(JsonNode? value, int count)
        => value is JsonArray array && array.Count == count ? array : throw new FormatException("Expected an array of " + count + ".");
}
