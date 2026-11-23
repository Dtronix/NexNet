using System.Globalization;
using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.Model;

/// <summary>
/// The Compare detail panel's wording (PKG compare: "Field diff · Invocation #812 SendAttachment", "3 differences"):
/// what an entity is called, what each compared value of it is called, and a field's own name out of its path.
/// </summary>
public static class FieldDiffText
{
    /// <summary>The Field diff subtitle with no entity picked.</summary>
    public const string NoEntity = "Pick an entity to compare";

    /// <summary>What an absent value shows in a file column.</summary>
    public const string Absent = "—";

    /// <summary>The kind of an entity, as the subtitle opens with it.</summary>
    public static string KindName(CompareEntityKind kind) => kind switch
    {
        CompareEntityKind.Invocation => "Invocation",
        CompareEntityKind.Pipe => "Pipe",
        CompareEntityKind.Collection => "Collection",
        CompareEntityKind.Control => "Control",
        _ => "Handshake",
    };

    /// <summary>
    /// An entity named for the Field diff subtitle: its kind, an invocation's id as A recorded it (B's when only B
    /// has it), and its label ("Invocation #812 SendAttachment", "Pipe FileUpload", "Handshake ClientGreeting").
    /// </summary>
    /// <param name="compare">The compare.</param>
    /// <param name="match">Index in <see cref="CompareSession.Matches"/>.</param>
    public static string Describe(CompareSession compare, int match)
    {
        ArgumentNullException.ThrowIfNull(compare);
        EntityMatch row = compare.Matches[match];
        string label = compare.GetLabel(match);
        string kind = KindName(row.Key.Kind);
        if (row.Key.Kind != CompareEntityKind.Invocation)
            return kind + " " + label;

        CompareSide side = row.A.IsPresent ? CompareSide.A : CompareSide.B;
        var session = compare.Get(side);
        int index = row.Get(side).Index;
        lock (session.Gate)
        {
            var invocations = session.Assembler.Invocations;
            if ((uint)index < (uint)invocations.Length)
                return kind + " #" + invocations[index].InvocationId.ToString(CultureInfo.InvariantCulture) + " " + label;
        }

        return kind + " " + label;
    }

    /// <summary>
    /// What one compared value of an entity is called on its part row: "Arguments", "Result", "Greeting", or for an item
    /// of a sequence its 1-based position and flow ("Item 3 · client → server").
    /// </summary>
    public static string PartLabel(in FieldDiffPart part)
    {
        string? item = part.Kind switch
        {
            FieldDiffPartKind.Arguments => null,
            FieldDiffPartKind.Result => null,
            FieldDiffPartKind.Greeting => null,
            FieldDiffPartKind.ChannelItem => "Item ",
            FieldDiffPartKind.CollectionMessage => "Message ",
            _ => "Write ",
        };
        if (item is null)
        {
            return part.Kind switch
            {
                FieldDiffPartKind.Arguments => "Arguments",
                FieldDiffPartKind.Result => "Result",
                _ => "Greeting",
            };
        }

        return item + (part.Ordinal + 1).ToString("N0", CultureInfo.InvariantCulture) + " · " + FlowName(part.Flow);
    }

    /// <summary>A wire flow in words.</summary>
    public static string FlowName(WireFlow flow) => flow == WireFlow.ClientToServer ? "client → server" : "server → client";

    /// <summary>A field's own name: the last segment of its path ("Text" of "message.Text", "[2]" of "Items[2]").</summary>
    public static string FieldName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int start = Math.Max(path.LastIndexOf('.') + 1, path.LastIndexOf('['));
        return start <= 0 ? path : path[start..];
    }

    /// <summary>The count at the right of the Field diff header ("3 differences", "No differences").</summary>
    public static string Differences(int count) => count switch
    {
        0 => "No differences",
        1 => "1 difference",
        _ => count.ToString("N0", CultureInfo.InvariantCulture) + " differences",
    };
}
