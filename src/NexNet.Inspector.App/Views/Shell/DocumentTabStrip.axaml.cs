using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Inspect;

namespace NexNet.Inspector.App.Views.Shell;

/// <summary>
/// The document tabs' list: a <see cref="TabStrip"/> (a tab list to assistive technology) that selects nothing on its own.
/// TabStrip's default AlwaysSelected would select the first tab on load and so activate a document nobody picked; the
/// selection here only mirrors <see cref="DocumentsViewModel.Active"/>.
/// </summary>
public sealed class DocumentTabList : TabStrip
{
    public DocumentTabList() => SelectionMode = SelectionMode.Single;

    // The strip's look is its DocumentTabs theme, keyed like a TabStrip's.
    protected override Type StyleKeyOverride => typeof(TabStrip);
}

/// <summary>
/// The tab strip's input: a click on a tab activates its document (the strip's selection only mirrors
/// <see cref="DocumentsViewModel.Active"/>), the close glyph and a middle-click close it, and a right-click opens the
/// tab's menu: Detach from session (a live tab that streams), Save .nntl…, Close.
/// </summary>
public sealed partial class DocumentTabStrip : UserControl
{
    /// <summary>Defines <see cref="IsCompare"/>.</summary>
    public static readonly StyledProperty<bool> IsCompareProperty = AvaloniaProperty.Register<DocumentTabStrip, bool>(nameof(IsCompare));

    // The tab a middle button went down on: the close happens on release over the same tab, as browsers do.
    private DocumentViewModel? _middlePressed;

    public DocumentTabStrip()
    {
        InitializeComponent();
        Tabs.SelectionChanged += OnSelectionChanged;
        Tabs.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
        Tabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel);
        Tabs.ContextRequested += OnTabContextRequested;
    }

    /// <summary>The tab menu last opened (tests read it).</summary>
    public ContextMenu? TabMenu { get; private set; }

    /// <summary>
    /// Builds the tab menu for <paramref name="document"/>: its title, Detach from session (live and streaming: stops the
    /// stream, the data stays), Save .nntl… (through the documents' save handler) and Close (which asks first when the
    /// data is unsaved, OQ5).
    /// </summary>
    public ContextMenu? CreateTabMenu(DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Documents is not { } documents) return null;
        ContextMenu menu = new();
        menu.Items.Add(InspectMenus.Header(null, document.Title, null, monoDetail: false));
        menu.Items.Add(new Separator());
        if (document is LiveDocumentViewModel live)
            menu.Items.Add(InspectMenus.Item(menu, "Detach from session", null, live.DetachAsync, enabled: live.IsStreaming));
        menu.Items.Add(InspectMenus.Item(menu, "Save .nntl…", null, async () =>
        {
            if (documents.SaveHandler is { } save) await save(document).ConfigureAwait(true);
        }, enabled: document.Session is not null && documents.SaveHandler is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(InspectMenus.Item(menu, "Close", null, () => Close(document)));
        return menu;
    }

    private void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DocumentAt(e.Source) is not { } document || CreateTabMenu(document) is not { } menu) return;
        e.Handled = true;
        TabMenu = menu;
        menu.Open(ContainerFor(document) as Control ?? Tabs);
    }

    /// <summary>True in the Compare workspace: each tab shows its inclusion checkbox.</summary>
    public bool IsCompare
    {
        get => GetValue(IsCompareProperty);
        set => SetValue(IsCompareProperty, value);
    }

    /// <summary>The tab container of a document, once realized (tests and the gallery force its states).</summary>
    public TabStripItem? ContainerFor(DocumentViewModel document) =>
        Tabs.ContainerFromItem(document) as TabStripItem;

    private DocumentsViewModel? Documents => (DataContext as ShellChromeViewModel)?.Documents;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is DocumentViewModel picked && Documents is { } documents && documents.Active != picked)
            documents.Activate(picked);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentViewModel document) Close(document);
        e.Handled = true;
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed) return;
        _middlePressed = DocumentAt(e.Source);
        // A middle press neither selects nor focuses the tab it closes.
        if (_middlePressed is not null) e.Handled = true;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle) return;
        DocumentViewModel? pressed = _middlePressed;
        _middlePressed = null;
        if (pressed is not null && DocumentAt(e.Source) == pressed)
        {
            e.Handled = true;
            Close(pressed);
        }
    }

    private static DocumentViewModel? DocumentAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TabStripItem>(includeSelf: true)?.DataContext as DocumentViewModel;

    private void Close(DocumentViewModel document)
    {
        // The command asks the unsaved-close prompt first (OQ5); without one the document closes.
        if (Documents?.CloseDocumentCommand is { } close && close.CanExecute(document)) close.Execute(document);
    }
}
