using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace NexNet.Inspector.App.Controls;

/// <summary>The three layouts of controls/pane-header.</summary>
public enum PaneHeaderVariant : byte
{
    /// <summary>A stream pane: Events | Ladder | Hex stream tabs, view controls, synced badge, split button.</summary>
    Tabbed = 0,

    /// <summary>The inspector: Decoded | Message trace (Handshake) tabs and the selected frame's id at the right.</summary>
    Inspector = 1,

    /// <summary>A Compare pane: a bold title and a secondary subtitle instead of tabs.</summary>
    Titled = 2,
}

/// <summary>The link badge of a pane header.</summary>
public enum PaneSyncState : byte
{
    /// <summary>No badge.</summary>
    None = 0,

    /// <summary>"synced" in status.ok with a link icon: the view follows the timeline's playhead and selection.</summary>
    Synced = 1,

    /// <summary>"unsynced" in tertiary text: the view is detached from the timeline.</summary>
    Unsynced = 2,
}

/// <summary>What a pane header's split button does (and so what it reads).</summary>
public enum PaneSplitAction : byte
{
    /// <summary>No split button.</summary>
    None = 0,

    /// <summary>"◫ Single" on the left pane while both show: collapse to one pane.</summary>
    Single = 1,

    /// <summary>"◫ Split" on the left pane while it shows alone: restore the right pane.</summary>
    Split = 2,

    /// <summary>"✕" on the right pane: close it.</summary>
    Close = 3,
}

/// <summary>
/// The uniform 24 px header of every pane (controls/pane-header): view tabs (the tab label is the pane's title, so no
/// separate title is drawn next to them), a slot for the view's own controls, the synced badge and the split button; or,
/// <see cref="PaneHeaderVariant.Titled"/>, a title and subtitle. The variant, badge and split action are pseudo-classes
/// (<c>:tabbed</c>, <c>:inspector</c>, <c>:titled</c>; <c>:synced</c>, <c>:unsynced</c>; <c>:single</c>, <c>:split</c>,
/// <c>:close</c>) that Themes/PaneHeader.axaml lays out. Tabs are a list of labels and a selected index, so the
/// view-model keeps its enum (an <c>EnumChoice</c>) and never compares labels.
/// </summary>
[PseudoClasses(":tabbed", ":inspector", ":titled", ":synced", ":unsynced", ":single", ":split", ":close")]
[TemplatePart("PART_Tabs", typeof(ListBox))]
public sealed class PaneHeader : TemplatedControl
{
    public static readonly StyledProperty<PaneHeaderVariant> VariantProperty =
        AvaloniaProperty.Register<PaneHeader, PaneHeaderVariant>(nameof(Variant));

    public static readonly StyledProperty<IReadOnlyList<string>?> TabsProperty =
        AvaloniaProperty.Register<PaneHeader, IReadOnlyList<string>?>(nameof(Tabs));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<PaneHeader, int>(nameof(SelectedIndex), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PaneHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<PaneHeader, string?>(nameof(Subtitle));

    public static readonly StyledProperty<object?> ViewControlsProperty =
        AvaloniaProperty.Register<PaneHeader, object?>(nameof(ViewControls));

    public static readonly StyledProperty<string?> TrailingProperty =
        AvaloniaProperty.Register<PaneHeader, string?>(nameof(Trailing));

    public static readonly StyledProperty<PaneSyncState> SyncStateProperty =
        AvaloniaProperty.Register<PaneHeader, PaneSyncState>(nameof(SyncState));

    public static readonly StyledProperty<PaneSplitAction> SplitActionProperty =
        AvaloniaProperty.Register<PaneHeader, PaneSplitAction>(nameof(SplitAction));

    public static readonly StyledProperty<ICommand?> SplitCommandProperty =
        AvaloniaProperty.Register<PaneHeader, ICommand?>(nameof(SplitCommand));

    public static readonly StyledProperty<bool> CanSplitProperty =
        AvaloniaProperty.Register<PaneHeader, bool>(nameof(CanSplit), true);

    public PaneHeader()
    {
        UpdateVariant(PaneHeaderVariant.Tabbed);
        UpdateSync(PaneSyncState.None);
        UpdateSplit(PaneSplitAction.None);
    }

    /// <summary>Tabs, tabs with the frame id, or a title.</summary>
    public PaneHeaderVariant Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    /// <summary>The tab labels in display order.</summary>
    public IReadOnlyList<string>? Tabs
    {
        get => GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    /// <summary>The selected tab (two-way).</summary>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The titled variant's bold title.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The titled variant's secondary subtitle.</summary>
    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>The view's own controls at the right (11 px secondary: "18 px rows", "Filter ▾ · Follow live", …).</summary>
    public object? ViewControls
    {
        get => GetValue(ViewControlsProperty);
        set => SetValue(ViewControlsProperty, value);
    }

    /// <summary>Mono text at the far right: the inspector's selected frame id.</summary>
    public string? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    /// <summary>The link badge.</summary>
    public PaneSyncState SyncState
    {
        get => GetValue(SyncStateProperty);
        set => SetValue(SyncStateProperty, value);
    }

    /// <summary>What the split button does; <see cref="PaneSplitAction.None"/> hides it.</summary>
    public PaneSplitAction SplitAction
    {
        get => GetValue(SplitActionProperty);
        set => SetValue(SplitActionProperty, value);
    }

    /// <summary>Run by the split button.</summary>
    public ICommand? SplitCommand
    {
        get => GetValue(SplitCommandProperty);
        set => SetValue(SplitCommandProperty, value);
    }

    /// <summary>False disables the split button (the window is too narrow for two panes, OQ10).</summary>
    public bool CanSplit
    {
        get => GetValue(CanSplitProperty);
        set => SetValue(CanSplitProperty, value);
    }

    /// <summary>The split button's accessible name.</summary>
    public static string SplitName(PaneSplitAction action) => action == PaneSplitAction.Close ? "Close pane" : "Toggle split view";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VariantProperty) UpdateVariant(change.GetNewValue<PaneHeaderVariant>());
        else if (change.Property == SyncStateProperty) UpdateSync(change.GetNewValue<PaneSyncState>());
        else if (change.Property == SplitActionProperty) UpdateSplit(change.GetNewValue<PaneSplitAction>());
    }

    private void UpdateVariant(PaneHeaderVariant variant)
    {
        PseudoClasses.Set(":tabbed", variant == PaneHeaderVariant.Tabbed);
        PseudoClasses.Set(":inspector", variant == PaneHeaderVariant.Inspector);
        PseudoClasses.Set(":titled", variant == PaneHeaderVariant.Titled);
    }

    private void UpdateSync(PaneSyncState state)
    {
        PseudoClasses.Set(":synced", state == PaneSyncState.Synced);
        PseudoClasses.Set(":unsynced", state == PaneSyncState.Unsynced);
    }

    private void UpdateSplit(PaneSplitAction action)
    {
        PseudoClasses.Set(":single", action == PaneSplitAction.Single);
        PseudoClasses.Set(":split", action == PaneSplitAction.Split);
        PseudoClasses.Set(":close", action == PaneSplitAction.Close);
    }
}
