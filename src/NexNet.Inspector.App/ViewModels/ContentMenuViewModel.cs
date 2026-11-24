using System.Buffers;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>Which bytes of an item a binary action uses.</summary>
public enum ContentByteSource : byte
{
    /// <summary>The item's own bytes: the whole frame, the field's range.</summary>
    Item = 0,

    /// <summary>For a frame of a reassembled message: the message's body, its chunks end to end.</summary>
    MessageBody = 1,
}

/// <summary>
/// A content view's context menu (Events, Ladder, Message trace, the property trees, Compare's field diff and entities,
/// the session list, timeline marks): what it does with the item right-clicked. Text: Copy, Copy value, Copy path,
/// Copy row as TSV. Binary, when the item maps to bytes: Copy as hex / C# / Base64, Save bytes to file… (a frame of a
/// reassembled message offers its frame bytes and the message body), and Open as ▸, which selects the bytes and reads
/// them in the inspector as the hex menu's "Decode selection as ▸" does. Made when the menu opens.
/// </summary>
public sealed class ContentMenuViewModel
{
    /// <summary>The most bytes copied or saved at once (the hex menu's bound).</summary>
    public const int MaxBytes = HexSelectionViewModel.MaxBytes;

    private readonly CaptureSession? _session;
    private readonly SyncBus? _sync;
    private readonly Func<string, Task>? _copy;
    private readonly Func<string, Task<Stream?>>? _saveTarget;

    /// <param name="item">The item.</param>
    /// <param name="session">The session its bytes are read from; null for a text-only item.</param>
    /// <param name="sync">The sync bus Open as ▸ selects on; null disables it.</param>
    /// <param name="copy">Puts text on the clipboard; null disables the copy items.</param>
    /// <param name="saveTarget">Asks where to save (a save picker) given a suggested file name; null disables Save.</param>
    public ContentMenuViewModel(ContentItem item, CaptureSession? session, SyncBus? sync, Func<string, Task>? copy,
        Func<string, Task<Stream?>>? saveTarget)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        _session = session;
        _sync = sync;
        _copy = copy;
        _saveTarget = saveTarget;
    }

    /// <summary>The item.</summary>
    public ContentItem Item { get; }

    /// <summary>True when the copy items can run.</summary>
    public bool CanCopy => _copy is not null;

    /// <summary>True when the item maps to bytes the session can read.</summary>
    public bool HasBytes => !Item.Bytes.IsNone && _session is not null;

    /// <summary>True when the item is a frame of a reassembled message: its body can be saved as well.</summary>
    public bool HasMessageBody => HasBytes && Item.Bytes.Kind == ContentBytesKind.Frame && !Item.Bytes.Message.IsNone;

    /// <summary>True when the bytes are contiguous and Open as ▸ can select them.</summary>
    public bool CanOpenAs => HasBytes && _sync is not null && Item.Bytes.Body.Length > 0;

    /// <summary>True when Save bytes to file… can ask where.</summary>
    public bool CanSave => HasBytes && _saveTarget is not null;

    /// <summary>The bytes of <paramref name="source"/>, copied out of the capture; null when they are not all held.</summary>
    public byte[]? ReadBytes(ContentByteSource source = ContentByteSource.Item)
    {
        if (!HasBytes || _session is not { } session) return null;
        var bytes = Item.Bytes;
        lock (session.Gate)
        {
            if (source == ContentByteSource.MessageBody)
                return HasMessageBody && MessageLength(session.Assembler, bytes.Message) is var length and >= 0
                    ? ReadPayload(session.Assembler, new PayloadRange(bytes.Message, 0, length))
                    : null;
            if (bytes.Kind == ContentBytesKind.Payload)
                return ReadPayload(session.Assembler, bytes.Payload);
            if (bytes.Range.Length is <= 0 or > MaxBytes) return null;
            var buffer = new byte[(int)bytes.Range.Length];
            return session.GetStore(bytes.Direction).TryCopyTo(bytes.Range.Offset, buffer) ? buffer : null;
        }
    }

    /// <summary>The bytes as <paramref name="format"/> text; null when they cannot be read.</summary>
    public string? Format(ByteCopyFormat format, ContentByteSource source = ContentByteSource.Item) => ReadBytes(source) is { } bytes
        ? format switch
        {
            ByteCopyFormat.CSharp => ByteText.CSharp(bytes),
            ByteCopyFormat.Base64 => ByteText.Base64(bytes),
            _ => ByteText.Hex(bytes),
        }
        : null;

    /// <summary>Copy (Ctrl+C): the item as displayed.</summary>
    public Task<bool> CopyTextAsync() => CopyAsync(Item.Text);

    /// <summary>Copy value.</summary>
    public Task<bool> CopyValueAsync() => CopyAsync(Item.Value);

    /// <summary>Copy path.</summary>
    public Task<bool> CopyPathAsync() => CopyAsync(Item.Path);

    /// <summary>Copy row as TSV.</summary>
    public Task<bool> CopyRowAsync() => CopyAsync(Item.Row);

    /// <summary>Copy as hex / C# byte array / Base64.</summary>
    public Task<bool> CopyBytesAsync(ByteCopyFormat format, ContentByteSource source = ContentByteSource.Item) => CopyAsync(Format(format, source));

    /// <summary>The file name Save suggests: "inbound-frame-1204.bin", "…-body.bin" for a message body.</summary>
    public string SaveName(ContentByteSource source) => Item.FileStem + (source == ContentByteSource.MessageBody ? "-body" : "") + ".bin";

    /// <summary>Save bytes to file…: asks where, then writes the raw bytes; false when canceled or unreadable.</summary>
    public async Task<bool> SaveAsync(ContentByteSource source = ContentByteSource.Item)
    {
        if (_saveTarget is not { } target || ReadBytes(source) is not { } bytes) return false;
        var stream = await target(SaveName(source)).ConfigureAwait(true);
        if (stream is null) return false;
        await using (stream.ConfigureAwait(true))
        {
            await stream.WriteAsync(bytes).ConfigureAwait(true);
            await stream.FlushAsync().ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>
    /// Open as ▸: selects the item's bytes (the hex view scrolls there) and reads them in the inspector as
    /// <paramref name="mode"/>, exactly as the hex menu's "Decode selection as ▸".
    /// </summary>
    public bool OpenAs(DecodeAs mode)
    {
        if (!CanOpenAs || _sync is not { } sync || _session is not { } session) return false;
        var bytes = Item.Bytes;
        long ticks;
        lock (session.Gate)
            ticks = session.TryGetFrame(new FrameKey(bytes.Direction, FrameSeq(session, bytes)), out var frame) ? frame.StartTicks : sync.Playhead;
        sync.Select(Model.Selection.OfBytes(bytes.Direction, bytes.Body), ticks);
        sync.SetDecode(mode);
        return true;
    }

    private async Task<bool> CopyAsync(string? text)
    {
        if (_copy is not { } copy || text is null) return false;
        await copy(text).ConfigureAwait(true);
        return true;
    }

    // The frame that holds the first body byte (for the playhead); -1 when none does.
    private static long FrameSeq(CaptureSession session, in ContentBytes bytes)
    {
        var frames = session.Assembler.GetFrames(bytes.Direction);
        var lo = 0;
        var hi = frames.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (frames[mid].Offset + frames[mid].Length <= bytes.Body.Offset)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo < frames.Length ? frames[lo].Seq : -1;
    }

    private static long MessageLength(MessageAssembler assembler, in MessageRef message) => message.Kind switch
    {
        MessageRefKind.Assembled when (uint)message.Index < (uint)assembler.Messages.Length => assembler.Messages[message.Index].Length,
        MessageRefKind.ChannelItem when (uint)message.Index < (uint)assembler.ChannelItems.Length => assembler.ChannelItems[message.Index].Length,
        _ => -1,
    };

    private static byte[]? ReadPayload(MessageAssembler assembler, in PayloadRange range)
    {
        if (range.IsNone || range.Length > MaxBytes || !assembler.TryGetPayload(range, out var sequence)) return null;
        return sequence.ToArray();
    }
}
