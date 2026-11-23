using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Inspect workspace: the timeline on top, two lower stream panes (split, or the left one alone) and the inspector
/// on the right, all following the active document and the shared sync bus. The window's width can close the right
/// pane and narrow the inspector (OQ10, <see cref="ShellChromeViewModel.Layout"/>); the user's split choice is kept and
/// applies again once the window is wide enough.
/// </summary>
public sealed partial class InspectWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly DocumentsViewModel _documents;
    private readonly ShellChromeViewModel _chrome;

    /// <param name="documents">The open documents and their sync bus.</param>
    /// <param name="chrome">The shell: workspace switch and collapse state.</param>
    /// <param name="paneFactory">Makes a stream view's model for a pane; by default the Events, Ladder or Hex view-model over the documents.</param>
    public InspectWorkspaceViewModel(DocumentsViewModel documents, ShellChromeViewModel chrome, Func<StreamPaneKind, PaneSide, object>? paneFactory = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(chrome);
        _documents = documents;
        _chrome = chrome;
        Func<StreamPaneKind, PaneSide, object> factory = paneFactory ?? ((kind, _) => kind switch
        {
            StreamPaneKind.Events => new EventsViewModel(documents),
            StreamPaneKind.Ladder => new LadderViewModel(documents),
            // A hex pane opens its dialogs ("Go to offset…") through whatever host the workspace has when it asks.
            _ => new HexViewModel(documents) { ShowDialog = dialog => ShowDialog?.Invoke(dialog) ?? false },
        });
        Timeline = new TimelineViewModel(documents);
        Inspector = new InspectorViewModel(documents);
        // The package's inspect screens: Ladder on the left, Hex stream on the right.
        Left = new StreamPaneViewModel(PaneSide.Left, StreamPaneKind.Ladder, factory);
        Right = new StreamPaneViewModel(PaneSide.Right, StreamPaneKind.Hex, factory);
        Right.SplitAction = PaneSplitAction.Close;
        Left.SplitCommand = ToggleSplitCommand;
        Right.SplitCommand = ClosePaneCommand;
        UpdateSplit();
        documents.PropertyChanged += OnDocumentsChanged;
        chrome.PropertyChanged += OnChromeChanged;
    }

    /// <summary>The timeline across the top.</summary>
    public TimelineViewModel Timeline { get; }

    /// <summary>The left lower pane.</summary>
    public StreamPaneViewModel Left { get; }

    /// <summary>The right lower pane.</summary>
    public StreamPaneViewModel Right { get; }

    /// <summary>The inspector at the right edge.</summary>
    public InspectorViewModel Inspector { get; }

    /// <summary>The user's choice: two lower panes (true) or one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRightPane))]
    public partial bool IsSplit { get; set; } = true;

    /// <summary>True when the right pane shows: split, and the window wide enough (OQ10).</summary>
    public bool ShowsRightPane => IsSplit && _chrome.ShowsRightPane;

    /// <summary>False while the window is too narrow for two panes: the Split button is disabled.</summary>
    public bool CanSplit => _chrome.ShowsRightPane;

    /// <summary>The inspector's width (324, or 280 when narrowed).</summary>
    public double InspectorWidth => _chrome.InspectorWidth;

    /// <summary>True when the workspace shows: a document is open and Inspect is the workspace.</summary>
    public bool IsShown => _documents.HasDocuments && !_chrome.IsCompare;

    /// <summary>Shows a dialog over the window (the hex menu's "Go to offset…"); false when one is already open.</summary>
    public Func<GoToOffsetDialogViewModel, bool>? ShowDialog { get; set; }

    /// <summary>
    /// Brings a view into a lower pane (the lane menu's "Show in Ladder" / "Show in Hex stream"): a pane that shows it
    /// already keeps it; otherwise the Hex stream takes the right pane when it shows, anything else the left.
    /// </summary>
    public void Show(StreamPaneKind kind)
    {
        if (Left.View.Value == kind || (ShowsRightPane && Right.View.Value == kind)) return;
        StreamPaneViewModel pane = kind == StreamPaneKind.Hex && ShowsRightPane ? Right : Left;
        pane.View.Value = kind;
    }

    /// <summary>The left pane's ◫ Single / ◫ Split.</summary>
    [RelayCommand]
    private void ToggleSplit() => IsSplit = !IsSplit;

    /// <summary>The right pane's ✕.</summary>
    [RelayCommand]
    private void ClosePane() => IsSplit = false;

    public void Dispose()
    {
        _documents.PropertyChanged -= OnDocumentsChanged;
        _chrome.PropertyChanged -= OnChromeChanged;
        Timeline.Dispose();
        Inspector.Dispose();
        Left.Dispose();
        Right.Dispose();
    }

    partial void OnIsSplitChanged(bool value) => UpdateSplit();

    private void UpdateSplit()
    {
        Left.SplitAction = ShowsRightPane ? PaneSplitAction.Single : PaneSplitAction.Split;
        Left.CanSplit = CanSplit;
    }

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentsViewModel.HasDocuments)) OnPropertyChanged(nameof(IsShown));
    }

    private void OnChromeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellChromeViewModel.Layout):
                OnPropertyChanged(nameof(ShowsRightPane));
                OnPropertyChanged(nameof(CanSplit));
                OnPropertyChanged(nameof(InspectorWidth));
                UpdateSplit();
                break;
            case nameof(ShellChromeViewModel.IsCompare):
                OnPropertyChanged(nameof(IsShown));
                break;
        }
    }
}
