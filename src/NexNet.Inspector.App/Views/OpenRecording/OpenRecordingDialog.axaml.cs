using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Inspect;

namespace NexNet.Inspector.App.Views.OpenRecording;

/// <summary>
/// The open-recording dialog's view. The two warning cards' texts mix the UI font with mono runs (hashes, the schema
/// path, the repair command), so they are built here from the view-model's pieces. A session row opens the content
/// menu on a right-click (Copy, Copy value, Copy row as TSV) and Ctrl+C copies it.
/// </summary>
public sealed partial class OpenRecordingDialog : UserControl
{
    private OpenRecordingDialogViewModel? _model;

    public OpenRecordingDialog()
    {
        InitializeComponent();
        Sessions.ContextRequested += OnSessionsContextRequested;
        Sessions.AddHandler(KeyDownEvent, OnSessionsKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The content menu last opened (tests read it).</summary>
    public ContextMenu? ContentMenu { get; private set; }

    /// <summary>The session row at <paramref name="index"/> as a content item; null outside the list.</summary>
    public ContentItem? Describe(int index) =>
        _model?.Source is { } source && (uint)index < (uint)source.Count ? ContentItems.ForSession(source[index]) : null;

    private void OnSessionsContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        int index = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
            ? Sessions.IndexFromContainer(container)
            : Sessions.SelectedIndex;
        if (Describe(index) is not { } item) return;
        e.Handled = true;
        ContextMenu menu = ContentMenus.Build(new ContentMenuViewModel(item, null, null, ViewServices.CopyTo(this), null));
        ContentMenu = menu;
        menu.Open(Sessions);
    }

    private void OnSessionsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (e.KeyModifiers & KeyModifiers.Control) == 0 || Describe(Sessions.SelectedIndex) is not { } item) return;
        e.Handled = true;
        ViewServices.Run(() => ViewServices.Copy(this, item.Text), "copy");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as OpenRecordingDialogViewModel;
        if (_model is null) return;
        _model.PropertyChanged += OnModelChanged;
        Fill(DamageText, _model.DamageDetail);
        Fill(SchemaText, _model.SchemaDetail);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenRecordingDialogViewModel.SchemaDetail) && _model is not null)
            Fill(SchemaText, _model.SchemaDetail);
    }

    private static void Fill(TextBlock target, IReadOnlyList<TextPiece> pieces)
    {
        InlineCollection inlines = target.Inlines ??= [];
        inlines.Clear();
        foreach (TextPiece piece in pieces)
        {
            Run run = new(piece.Text);
            if (piece.Mono) run.Classes.Add("mono");
            inlines.Add(run);
        }
    }
}
