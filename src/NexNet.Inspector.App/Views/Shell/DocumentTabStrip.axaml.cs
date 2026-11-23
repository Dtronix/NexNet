using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NexNet.Inspector.App.ViewModels;

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
/// <see cref="DocumentsViewModel.Active"/>), the close glyph and a middle-click close it.
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
