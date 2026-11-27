using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Model;

/// <summary>How the inspector reads a hex selection (the hex menu's "Decode selection as ▸").</summary>
public enum DecodeAs : byte
{
    /// <summary>As the schema decodes the frame that holds it: the inspector's usual tree.</summary>
    Auto = 0,

    /// <summary>One MessagePack value, walked by its wire shape alone.</summary>
    MessagePack = 1,

    /// <summary>UTF-8 text.</summary>
    Utf8 = 2,

    /// <summary>A LEB128 length prefix and the MessagePack item it measures (a typed channel's item framing).</summary>
    Leb128 = 3,

    /// <summary>The bytes alone, 16 to a row.</summary>
    Raw = 4,
}

/// <summary>
/// A decode the user asked for on a hex selection: how, and which bytes. It lasts while the selection stays inside
/// those bytes (a click on one of its decoded rows keeps it) and ends with any other selection.
/// </summary>
/// <param name="Mode">How the bytes are read.</param>
/// <param name="Direction">The stream they are in.</param>
/// <param name="Bytes">The bytes.</param>
public readonly record struct SelectionDecode(DecodeAs Mode, TapDirection Direction, StreamRange Bytes)
{
    /// <summary>The inspector's own decode.</summary>
    public static SelectionDecode Auto => new(DecodeAs.Auto, TapDirection.Inbound, default);

    /// <summary>True for <see cref="Auto"/>.</summary>
    public bool IsAuto => Mode == DecodeAs.Auto;

    /// <summary>True when <paramref name="selection"/> is a byte range inside these bytes.</summary>
    public bool Covers(in Selection selection) =>
        !IsAuto && selection.Kind == SelectionKind.ByteRange && selection.Direction == Direction
        && selection.Bytes.Offset >= Bytes.Offset && selection.Bytes.End <= Bytes.End;
}
