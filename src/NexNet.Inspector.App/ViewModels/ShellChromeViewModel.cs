using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The window chrome's state: the workspace switch, the document tabs (through <see cref="Documents"/>), the collapse
/// state the window's width puts the panes in (OQ10), and the requests the title bar raises for the shell to handle
/// (app menu, Connect, Open). It holds no theme or settings, so a gallery or test builds it from documents alone.
/// </summary>
public sealed partial class ShellChromeViewModel : ObservableObject
{
    /// <param name="documents">The open documents the tab strip shows.</param>
    public ShellChromeViewModel(DocumentsViewModel documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        Documents = documents;
        Workspace = new EnumChoice<Workspace>([(Model.Workspace.Inspect, "Inspect"), (Model.Workspace.Compare, "Compare")], Model.Workspace.Inspect);
        Workspace.ValueChanged += (_, _) => OnPropertyChanged(nameof(IsCompare));
        Session = new SessionAreaViewModel(documents);
        TimeWindow = new TimeWindowReadoutViewModel(documents);
    }

    /// <summary>The connection group, record button and session popover.</summary>
    public SessionAreaViewModel Session { get; }

    /// <summary>The − range + readout.</summary>
    public TimeWindowReadoutViewModel TimeWindow { get; }

    /// <summary>The open documents, the active one and Ctrl+Tab order.</summary>
    public DocumentsViewModel Documents { get; }

    /// <summary>Inspect or Compare (the title bar's segmented control).</summary>
    public EnumChoice<Workspace> Workspace { get; }

    /// <summary>True in the Compare workspace: the tabs show their inclusion checkboxes.</summary>
    public bool IsCompare => Workspace.Value == Model.Workspace.Compare;

    /// <summary>Which panes show at the window's width.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRightPane), nameof(InspectorWidth))]
    public partial ShellLayoutState Layout { get; private set; }

    /// <summary>True when the right lower pane shows.</summary>
    public bool ShowsRightPane => ShellLayoutPolicy.ShowsRightPane(Layout);

    /// <summary>The inspector's width (324, or 280 when narrowed).</summary>
    public double InspectorWidth => ShellLayoutPolicy.InspectorWidthFor(Layout);

    /// <summary>Raised when the title bar asks for something another part of the shell provides.</summary>
    public event Action<ShellRequest>? Requested;

    /// <summary>Applies the collapse rules for a new window width.</summary>
    public void UpdateWidth(double width) => Layout = ShellLayoutPolicy.For(width);

    /// <summary>The logo button, the Connect group and the tab strip's +.</summary>
    [RelayCommand]
    private void Request(ShellRequest request) => Requested?.Invoke(request);
}
