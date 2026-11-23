using System.Globalization;
using System.Text;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Model;

/// <summary>
/// The inspector's Decoded tab for a hex selection read another way ("Decode selection as ▸"): one section naming the
/// reading and the range, then the bytes as a MessagePack value (a schema-less walk), UTF-8 text, a LEB128 length and
/// the item it measures, or raw rows. Every row keeps its bytes, so hovering it lights them in the hex view; offsets are
/// the selection's frame's (offset 0 its first body byte, header bytes negative), as the inspector's other rows are.
/// Call it under the session's gate.
/// </summary>
public static class SelectionProjection
{
    /// <summary>The most bytes decoded; a longer selection decodes its start and says so.</summary>
    public const int MaxBytes = 64 * 1024;

    private const int RawRow = 16;
    private const int MaxLeb128Bytes = 5;
    private const char DeletePicture = (char)0x2421;
    private const char ControlPictures = (char)0x2400;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The section title of a reading.</summary>
    public static string Title(DecodeAs mode) => mode switch
    {
        DecodeAs.MessagePack => "MessagePack value",
        DecodeAs.Utf8 => "UTF-8 text",
        DecodeAs.Leb128 => "LEB128 length + item",
        DecodeAs.Raw => "Raw bytes",
        _ => "Auto (schema)",
    };

    /// <summary>Builds the content for <paramref name="decode"/>; null for the schema's own decode.</summary>
    public static InspectorContent? Build(CaptureSession session, in Selection selection, in SelectionDecode decode)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (decode.IsAuto) return null;

        StreamRange range = decode.Bytes;
        FrameKey frameKey = selection.Frame.Seq >= 0 && selection.Frame.Direction == decode.Direction ? selection.Frame : new FrameKey(decode.Direction, -1);
        long bodyOffset = range.Offset;
        if (frameKey.Seq >= 0 && session.TryGetFrame(frameKey, out FrameRecord frame)) bodyOffset = frame.BodyOffset;
        MessageRef message = frameKey.Seq >= 0 ? MessageRef.ForFrame(decode.Direction, frameKey.Seq) : MessageRef.None;
        PayloadMap map = PayloadMap.ForFrame(decode.Direction, frameKey.Seq, bodyOffset);
        long origin = range.Offset - bodyOffset;

        PropertyTreeModel tree = new();
        int length = (int)Math.Min(range.Length, MaxBytes);
        string summary = range.Length.ToString("N0", Invariant) + " B · " + ByteText.Range(range.Offset, range.Length);
        tree.Section(Title(decode.Mode), summary);
        byte[] bytes = new byte[length];
        if (!session.GetStore(decode.Direction).TryCopyTo(range.Offset, bytes))
        {
            tree.Field("Bytes", "bytes no longer held", PropertyTone.Muted, map.Map(origin, length));
        }
        else
        {
            if (range.Length > length)
                tree.Field("Note", "first " + length.ToString("N0", Invariant) + " B decoded", PropertyTone.Muted);
            switch (decode.Mode)
            {
                case DecodeAs.MessagePack:
                    tree.Decoded(session.Decoder.Decoder.DecodeMessagePack(bytes, new PayloadRange(message, origin, length), "value"), map, "bytes");
                    break;
                case DecodeAs.Utf8:
                    Utf8(tree, bytes, map, origin);
                    break;
                case DecodeAs.Leb128:
                    Leb128(tree, session.Decoder.Decoder, bytes, map, message, origin);
                    break;
                default:
                    Raw(tree, bytes, map, range.Offset, origin);
                    break;
            }
        }

        tree.Complete();
        string label = frameKey.Seq >= 0 ? "#" + frameKey.Seq.ToString("N0", Invariant) : range.Length.ToString("N0", Invariant) + " B";
        return new InspectorContent(frameKey, message, tree, label, false, null, true);
    }

    private static void Utf8(PropertyTreeModel tree, byte[] bytes, in PayloadMap map, long origin)
    {
        bool valid = System.Text.Unicode.Utf8.IsValid(bytes);
        string text = Encoding.UTF8.GetString(bytes);
        tree.Field("Text", Printable(text), valid ? PropertyTone.Normal : PropertyTone.Muted, map.Map(origin, bytes.Length));
        tree.Field("Characters", text.Length.ToString("N0", Invariant), PropertyTone.Muted);
        if (!valid) tree.Field("Note", "not valid UTF-8 · bad sequences shown as U+FFFD", PropertyTone.Muted);
    }

    // Control characters would break the row's single line: shown as their Unicode control pictures.
    private static string Printable(string text)
    {
        bool clean = true;
        foreach (char c in text)
        {
            if (c < 0x20 || c == 0x7F)
            {
                clean = false;
                break;
            }
        }

        if (clean) return text;
        return string.Create(text.Length, text, static (chars, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                chars[i] = c < 0x20 ? (char)(ControlPictures + c) : c == 0x7F ? DeletePicture : c;
            }
        });
    }

    private static void Leb128(PropertyTreeModel tree, SchemaDecoder decoder, byte[] bytes, in PayloadMap map, MessageRef message, long origin)
    {
        ulong value = 0;
        int used = 0;
        bool ended = false;
        while (used < bytes.Length && used < MaxLeb128Bytes)
        {
            byte b = bytes[used];
            value |= (ulong)(b & 0x7F) << (7 * used);
            used++;
            if ((b & 0x80) == 0)
            {
                ended = true;
                break;
            }
        }

        if (!ended || value > int.MaxValue)
        {
            tree.Field("Length", "not a LEB128 length", PropertyTone.Muted, map.Map(origin, used));
            return;
        }

        int length = (int)value;
        tree.Field("Length", length.ToString("N0", Invariant) + " · LEB128 " + used.ToString(Invariant) + " B", PropertyTone.Normal, map.Map(origin, used));
        int available = Math.Min(length, bytes.Length - used);
        if (available < length)
        {
            tree.Field("Note", "item cut short: " + available.ToString("N0", Invariant) + " of " + length.ToString("N0", Invariant) + " B selected",
                PropertyTone.Muted);
        }

        if (available <= 0) return;
        ReadOnlyMemory<byte> item = bytes.AsMemory(used, available);
        tree.Decoded(decoder.DecodeMessagePack(item, new PayloadRange(message, origin + used, available), "Item"), map, "Item");
        int after = used + available;
        if (after < bytes.Length)
        {
            tree.Field("Following", (bytes.Length - after).ToString("N0", Invariant) + " B after the item", PropertyTone.Muted,
                map.Map(origin + after, bytes.Length - after));
        }
    }

    private static void Raw(PropertyTreeModel tree, byte[] bytes, in PayloadMap map, long streamOffset, long origin)
    {
        for (int at = 0; at < bytes.Length; at += RawRow)
        {
            int count = Math.Min(RawRow, bytes.Length - at);
            tree.Field(ByteText.Offset(streamOffset + at), ByteText.Hex(bytes.AsSpan(at, count)), PropertyTone.Muted, map.Map(origin + at, count));
        }
    }
}
