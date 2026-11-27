using System.Buffers.Binary;

namespace NexNet.Inspector.Core.Schema;

internal enum TokenType : byte
{
    Nil,
    Boolean,
    UInt,
    Int,
    Float32,
    Float64,
    Str,
    Bin,
    Array,
    Map,
    Ext,
}

internal enum TokenResult : byte
{
    Ok,
    Truncated,
    Malformed,
}

/// <summary>
/// One MessagePack header read from a span: its type, how many bytes it spans and its immediate value. Unlike
/// MsgPackReader it never throws and reports exact positions, which is what a byte-range-preserving decoder needs.
/// </summary>
internal readonly struct MsgPackToken
{
    public TokenType Type { get; init; }

    /// <summary>Bytes before the payload: the code byte and any length, type or value bytes.</summary>
    public int HeaderLength { get; init; }

    /// <summary>Bytes of the token itself: header plus str/bin/ext payload; children of containers excluded.</summary>
    public int Length { get; init; }

    /// <summary>Array or map count.</summary>
    public int Count { get; init; }

    /// <summary>Integer value bits (UInt: unsigned, Int: signed), boolean (0/1) or float bits.</summary>
    public long Bits { get; init; }

    /// <summary>Ext type code.</summary>
    public sbyte ExtType { get; init; }

    public bool IsInteger => Type is TokenType.UInt or TokenType.Int;

    public static TokenResult TryRead(ReadOnlySpan<byte> s, int pos, out MsgPackToken token)
    {
        token = default;
        if (pos >= s.Length)
            return TokenResult.Truncated;

        var code = s[pos];
        var rest = s.Slice(pos + 1);
        switch (code)
        {
            case <= 0x7f:
                token = new MsgPackToken { Type = TokenType.UInt, HeaderLength = 1, Length = 1, Bits = code };
                return TokenResult.Ok;
            case <= 0x8f:
                return Container(TokenType.Map, 1, code & 0x0f, s.Length - pos, out token);
            case <= 0x9f:
                return Container(TokenType.Array, 1, code & 0x0f, s.Length - pos, out token);
            case <= 0xbf:
                return Payload(TokenType.Str, 1, code & 0x1f, s.Length - pos, out token);
            case 0xc0:
                token = new MsgPackToken { Type = TokenType.Nil, HeaderLength = 1, Length = 1 };
                return TokenResult.Ok;
            case 0xc1:
                return TokenResult.Malformed;
            case 0xc2:
            case 0xc3:
                token = new MsgPackToken { Type = TokenType.Boolean, HeaderLength = 1, Length = 1, Bits = code - 0xc2 };
                return TokenResult.Ok;
            case 0xc4:
            case 0xc5:
            case 0xc6:
            case 0xd9:
            case 0xda:
            case 0xdb:
            {
                var width = code is 0xc4 or 0xd9 ? 1 : code is 0xc5 or 0xda ? 2 : 4;
                if (!TryLength(rest, width, out var length))
                    return TokenResult.Truncated;
                return Payload(code <= 0xc6 ? TokenType.Bin : TokenType.Str, 1 + width, length, s.Length - pos, out token);
            }
            case 0xc7:
            case 0xc8:
            case 0xc9:
            {
                var width = code == 0xc7 ? 1 : code == 0xc8 ? 2 : 4;
                if (!TryLength(rest, width, out var length) || rest.Length < width + 1)
                    return TokenResult.Truncated;
                var result = Payload(TokenType.Ext, 2 + width, length, s.Length - pos, out token);
                token = token with { ExtType = (sbyte)rest[width] };
                return result;
            }
            case 0xca:
                if (rest.Length < 4)
                    return TokenResult.Truncated;
                token = new MsgPackToken { Type = TokenType.Float32, HeaderLength = 5, Length = 5, Bits = BinaryPrimitives.ReadInt32BigEndian(rest) };
                return TokenResult.Ok;
            case 0xcb:
                if (rest.Length < 8)
                    return TokenResult.Truncated;
                token = new MsgPackToken { Type = TokenType.Float64, HeaderLength = 9, Length = 9, Bits = BinaryPrimitives.ReadInt64BigEndian(rest) };
                return TokenResult.Ok;
            case 0xcc:
                return Integer(TokenType.UInt, rest, 1, out token);
            case 0xcd:
                return Integer(TokenType.UInt, rest, 2, out token);
            case 0xce:
                return Integer(TokenType.UInt, rest, 4, out token);
            case 0xcf:
                return Integer(TokenType.UInt, rest, 8, out token);
            case 0xd0:
                return Integer(TokenType.Int, rest, 1, out token);
            case 0xd1:
                return Integer(TokenType.Int, rest, 2, out token);
            case 0xd2:
                return Integer(TokenType.Int, rest, 4, out token);
            case 0xd3:
                return Integer(TokenType.Int, rest, 8, out token);
            case >= 0xd4 and <= 0xd8:
            {
                if (rest.Length < 1)
                    return TokenResult.Truncated;
                var result = Payload(TokenType.Ext, 2, 1 << (code - 0xd4), s.Length - pos, out token);
                token = token with { ExtType = (sbyte)rest[0] };
                return result;
            }
            case 0xdc:
            case 0xdd:
            case 0xde:
            case 0xdf:
            {
                var width = code is 0xdc or 0xde ? 2 : 4;
                if (!TryLength(rest, width, out var count))
                    return TokenResult.Truncated;
                return Container(code <= 0xdd ? TokenType.Array : TokenType.Map, 1 + width, count, s.Length - pos, out token);
            }
            default:
                token = new MsgPackToken { Type = TokenType.Int, HeaderLength = 1, Length = 1, Bits = (sbyte)code };
                return TokenResult.Ok;
        }
    }

    /// <summary>Skips one whole value, iteratively, so nesting depth uses no stack.</summary>
    public static TokenResult TrySkip(ReadOnlySpan<byte> s, ref int pos)
    {
        var position = pos;
        long pending = 1;
        while (pending > 0)
        {
            var result = TryRead(s, position, out var token);
            if (result != TokenResult.Ok)
                return result;
            position += token.Length;
            pending--;
            if (token.Type == TokenType.Array)
                pending += token.Count;
            else if (token.Type == TokenType.Map)
                pending += 2L * token.Count;
        }

        pos = position;
        return TokenResult.Ok;
    }

    private static bool TryLength(ReadOnlySpan<byte> rest, int width, out int length)
    {
        length = 0;
        if (rest.Length < width)
            return false;
        var value = width switch
        {
            1 => rest[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(rest),
            _ => BinaryPrimitives.ReadUInt32BigEndian(rest),
        };
        // A length past int.MaxValue cannot fit in any span this decoder reads.
        if (value > int.MaxValue)
            return false;
        length = (int)value;
        return true;
    }

    private static TokenResult Payload(TokenType type, int header, int length, int available, out MsgPackToken token)
    {
        token = new MsgPackToken { Type = type, HeaderLength = header, Length = header + length };
        return (long)header + length <= available ? TokenResult.Ok : TokenResult.Truncated;
    }

    // A count beyond the remaining bytes is not refused here: the elements that did arrive still decode, and the first
    // missing one ends the walk as truncated.
    private static TokenResult Container(TokenType type, int header, int count, int available, out MsgPackToken token)
    {
        token = new MsgPackToken { Type = type, HeaderLength = header, Length = header, Count = count };
        return header <= available ? TokenResult.Ok : TokenResult.Truncated;
    }

    private static TokenResult Integer(TokenType type, ReadOnlySpan<byte> rest, int width, out MsgPackToken token)
    {
        token = default;
        if (rest.Length < width)
            return TokenResult.Truncated;
        long bits = (type, width) switch
        {
            (TokenType.UInt, 1) => rest[0],
            (TokenType.UInt, 2) => BinaryPrimitives.ReadUInt16BigEndian(rest),
            (TokenType.UInt, 4) => BinaryPrimitives.ReadUInt32BigEndian(rest),
            (TokenType.UInt, _) => (long)BinaryPrimitives.ReadUInt64BigEndian(rest),
            (_, 1) => (sbyte)rest[0],
            (_, 2) => BinaryPrimitives.ReadInt16BigEndian(rest),
            (_, 4) => BinaryPrimitives.ReadInt32BigEndian(rest),
            _ => BinaryPrimitives.ReadInt64BigEndian(rest),
        };
        token = new MsgPackToken { Type = type, HeaderLength = 1 + width, Length = 1 + width, Bits = bits };
        return TokenResult.Ok;
    }
}
