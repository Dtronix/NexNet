using System.Globalization;
using System.Text;

namespace NexNet.Inspector.App.Model;

/// <summary>Bytes as the hex menu copies them: hex pairs, a C# byte array, Base64; and the menu header's range.</summary>
public static class ByteText
{
    private const string HexDigits = "0123456789ABCDEF";

    /// <summary>Space-separated uppercase pairs, as the hex view shows them ("01 72 00 10").</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return "";
        return string.Create(bytes.Length * 3 - 1, bytes.ToArray(), static (chars, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                int at = i * 3;
                chars[at] = HexDigits[source[i] >> 4];
                chars[at + 1] = HexDigits[source[i] & 0xF];
                if (at + 2 < chars.Length) chars[at + 2] = ' ';
            }
        });
    }

    /// <summary>A C# array initializer, 16 bytes to a line: <c>new byte[] { 0x01, 0x72 }</c>.</summary>
    public static string CSharp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return "new byte[] { }";
        StringBuilder text = new(16 + (bytes.Length * 6));
        text.Append("new byte[]").AppendLine().Append('{');
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i % 16 == 0) text.AppendLine().Append("    ");
            else text.Append(' ');
            text.Append("0x").Append(HexDigits[bytes[i] >> 4]).Append(HexDigits[bytes[i] & 0xF]);
            if (i < bytes.Length - 1) text.Append(',');
        }

        return text.AppendLine().Append('}').ToString();
    }

    /// <summary>Base64.</summary>
    public static string Base64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

    /// <summary>A stream offset as the hex view's gutter writes it: uppercase hex, at least six digits ("1A3F21").</summary>
    public static string Offset(long offset) => offset.ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>"16 bytes selected" (or "1 byte selected").</summary>
    public static string Count(long length) =>
        length.ToString("N0", CultureInfo.InvariantCulture) + (length == 1 ? " byte selected" : " bytes selected");

    /// <summary>The inclusive range: "1A3F21 – 1A3F30".</summary>
    public static string Range(long offset, long length) =>
        length <= 1 ? Offset(offset) : Offset(offset) + " – " + Offset(offset + length - 1);
}
