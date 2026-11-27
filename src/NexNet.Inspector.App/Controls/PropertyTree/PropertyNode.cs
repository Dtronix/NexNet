using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.PropertyTree;

/// <summary>The two kinds of property-tree row.</summary>
public enum PropertyRowKind : byte
{
    /// <summary>A 21 px raised row, "▾ Title" in semibold with a secondary summary; collapses its fields.</summary>
    Section = 0,

    /// <summary>A name / value row, indented 12 px per level.</summary>
    Field = 1,
}

/// <summary>The kind badge after a section's title: what rides on the duplex pipe the section names.</summary>
public enum PropertyBadge : byte
{
    /// <summary>No badge.</summary>
    None = 0,

    /// <summary>CHANNEL: the section is a typed channel.</summary>
    Channel = 1,

    /// <summary>COLLECTION: the section is a synchronized collection.</summary>
    Collection = 2,
}

/// <summary>The colour of a field's value.</summary>
public enum PropertyTone : byte
{
    /// <summary>Primary mono text.</summary>
    Normal = 0,

    /// <summary>A transport read or write range (split.transport).</summary>
    Transport = 1,

    /// <summary>A NexNet split or a redacted value (split.nexnet).</summary>
    NexNet = 2,

    /// <summary>Tertiary text: a value that is not there (raw without a schema, not captured).</summary>
    Muted = 3,
}

/// <summary>
/// Where a row's bytes are: in the coordinates of a message (what <see cref="App.Model.FieldHover"/> carries, so the hex
/// view lights the same bytes) and in its direction's stream (what a click selects). A row without bytes has
/// <see cref="Length"/> 0.
/// </summary>
/// <param name="Message">The message whose payload coordinates <paramref name="Offset"/> is in; for a frame's header and
/// transport rows the frame itself (<see cref="MessageRef.ForFrame"/>), whose coordinates start at its body, so header
/// bytes have a negative offset.</param>
/// <param name="Offset">The first byte in the message's coordinates.</param>
/// <param name="Length">The byte count.</param>
/// <param name="Direction">The stream the bytes travelled in.</param>
/// <param name="Stream">The bytes in that stream: the whole range when it is contiguous, else its first fragment's part.</param>
public readonly record struct PropertyBytes(MessageRef Message, long Offset, int Length, TapDirection Direction, StreamRange Stream)
{
    /// <summary>No bytes.</summary>
    public static PropertyBytes None => new(MessageRef.None, 0, 0, TapDirection.Inbound, default);

    /// <summary>True when the row has no bytes.</summary>
    public bool IsNone => Length <= 0;
}

/// <summary>
/// One node of a <see cref="PropertyTreeModel"/>, stored flat in pre-order: a node's descendants follow it up to
/// <see cref="SubtreeEnd"/>, so collapsing skips a range and flattening is one forward walk. Text is either fixed
/// (<see cref="Value"/>) or rendered on demand from a decoded field (<see cref="Decoded"/>, <see cref="FieldIndex"/>).
/// </summary>
public struct PropertyNode
{
    /// <summary>Section or field.</summary>
    public PropertyRowKind Kind;

    /// <summary>Nesting: 0 for a section, 1 for its fields, more for nested decoded fields.</summary>
    public int Depth;

    /// <summary>One past the node's last descendant.</summary>
    public int SubtreeEnd;

    /// <summary>The name column (or the section title).</summary>
    public string Name;

    /// <summary>The value column (or the section summary); null when it is rendered from the decoded field.</summary>
    public string? Value;

    /// <summary>Index of the decoded value in the model; -1 for a fixed value.</summary>
    public int Decoded;

    /// <summary>The decoded field's index in that value; -1 for none.</summary>
    public int FieldIndex;

    /// <summary>The value's colour.</summary>
    public PropertyTone Tone;

    /// <summary>The node's bytes.</summary>
    public PropertyBytes Bytes;

    /// <summary>True while its children show.</summary>
    public bool Expanded;

    /// <summary>A section's kind badge.</summary>
    public PropertyBadge Badge;

    /// <summary>True when the node has children.</summary>
    public readonly bool HasChildren(int index) => SubtreeEnd > index + 1;
}
