using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The workspace splitters' sizes (<see cref="PaneSizes"/>), shared by the Inspect and Compare workspaces and kept in
/// <c>settings.json</c>. A drag changes them live and saves once it ends; a double-click resets one and saves.
/// </summary>
public sealed partial class PaneSizesViewModel : ObservableObject
{
    private readonly SettingsStore? _store;

    /// <param name="store">Where the sizes are kept; null keeps them for this run only (tests, self-check).</param>
    public PaneSizesViewModel(SettingsStore? store)
    {
        _store = store;
        Sizes = (store?.Current.Panes ?? new PaneSizes()).Normalized();
    }

    /// <summary>The sizes now.</summary>
    [ObservableProperty]
    public partial PaneSizes Sizes { get; private set; }

    /// <summary>Applies a change; <paramref name="save"/> writes the settings (a drag saves once, when it ends).</summary>
    public void Update(Func<PaneSizes, PaneSizes> change, bool save)
    {
        ArgumentNullException.ThrowIfNull(change);
        PaneSizes next = change(Sizes).Normalized();
        if (next != Sizes) Sizes = next;
        if (save) Save();
    }

    /// <summary>Writes the sizes into the settings; false when the file could not be written.</summary>
    public bool Save() => _store?.Update(s => s with { Panes = Sizes }) ?? true;

    /// <summary>The timeline splitter's double-click: back to the lanes' own height.</summary>
    public void ResetTimeline() => Update(static s => s with { TimelineHeight = null }, save: true);

    /// <summary>The stream panes' splitter's double-click: equal widths.</summary>
    public void ResetLeftShare() => Update(static s => s with { LeftShare = PaneLayoutPolicy.DefaultLeftShare }, save: true);

    /// <summary>The inspector splitter's double-click: 324 px.</summary>
    public void ResetInspector() => Update(static s => s with { InspectorWidth = null }, save: true);

    /// <summary>Compare's sidebar splitter's double-click: 232 px.</summary>
    public void ResetCompareSidebar() => Update(static s => s with { CompareSidebarWidth = PaneLayoutPolicy.DefaultCompareSidebarWidth }, save: true);

    /// <summary>Compare's detail splitter's double-click: 200 px.</summary>
    public void ResetCompareDetail() => Update(static s => s with { CompareDetailHeight = PaneLayoutPolicy.DefaultCompareDetailHeight }, save: true);
}
