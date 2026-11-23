using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls.Compare;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>What a row of the Entities sidebar is.</summary>
public enum CompareEntityItemKind : byte
{
    /// <summary>A category: Invocations, Pipes · channels, Collections, Control, Handshake.</summary>
    Category = 0,

    /// <summary>A group of the union: one method's invocations, one pipe, one control type.</summary>
    Group = 1,

    /// <summary>The selected entity, first under its category (◆).</summary>
    Pinned = 2,
}

/// <summary>
/// One 20 px row of the Entities sidebar (screens/compare): a category with its caret, a group with the checkbox that
/// gives it a row in the aligned view, or the pinned entity; each with its Δ marker and count.
/// </summary>
public sealed partial class CompareEntityItem : ObservableObject
{
    private readonly Action<CompareEntityItem>? _shownChanged;

    /// <param name="kind">What the row is.</param>
    /// <param name="category">Its category.</param>
    /// <param name="group">For a group, its index in the compare's groups; -1 otherwise.</param>
    /// <param name="match">For the pinned row, the entity; -1 otherwise.</param>
    /// <param name="label">The label.</param>
    /// <param name="count">The mono count at the right ("2,418", "1.2k", "7 fr"); empty for none.</param>
    /// <param name="hasDelta">True when something in it differs between the files.</param>
    /// <param name="isExpanded">For a category, whether its groups are listed.</param>
    /// <param name="isShown">For a group, whether it has a row in the aligned view.</param>
    /// <param name="shownChanged">Told when the user ticks or unticks a group.</param>
    public CompareEntityItem(CompareEntityItemKind kind, CompareCategory category, int group, int match, string label, string count, bool hasDelta,
        bool isExpanded = false, bool isShown = true, Action<CompareEntityItem>? shownChanged = null)
    {
        Kind = kind;
        Category = category;
        Group = group;
        Match = match;
        Label = label;
        Count = count;
        HasDelta = hasDelta;
        IsExpanded = isExpanded;
        _isShown = isShown;
        _shownChanged = shownChanged;
    }

    public CompareEntityItemKind Kind { get; }

    public CompareCategory Category { get; }

    public int Group { get; }

    public int Match { get; }

    public string Label { get; }

    public string Count { get; }

    public bool HasDelta { get; }

    public bool IsExpanded { get; }

    public bool IsCategory => Kind == CompareEntityItemKind.Category;

    public bool IsGroup => Kind == CompareEntityItemKind.Group;

    public bool IsPinned => Kind == CompareEntityItemKind.Pinned;

    /// <summary>The caret: ▾ open, ▸ closed.</summary>
    public string Caret => IsExpanded ? "▾" : "▸";

    /// <summary>Children sit 16 px in from their category.</summary>
    public Thickness Indent => IsCategory ? new Thickness(4, 0, 0, 0) : new Thickness(20, 0, 0, 0);

    private bool _isShown;

    /// <summary>The group's checkbox: it has a row of its own under its expanded category.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (SetProperty(ref _isShown, value)) _shownChanged?.Invoke(this);
        }
    }
}
