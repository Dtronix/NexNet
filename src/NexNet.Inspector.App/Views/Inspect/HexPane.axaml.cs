using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Views.Inspect;

/// <summary>
/// The Hex stream pane's body; see HexPane.axaml. Code opens the selection menu on a right-click (selecting the field
/// under the pointer first when the click is outside the selection) and runs its shortcuts: Ctrl+C copies the bytes as
/// hex, Home goes to the frame's start, Ctrl+G to an offset, Ctrl+B bookmarks.
/// </summary>
public partial class HexPane : UserControl
{
    public HexPane()
    {
        InitializeComponent();
        View.ContextRequested += OnContextRequested;
    }

    private HexViewModel? Model => DataContext as HexViewModel;

    /// <summary>The menu last opened (tests and the capture read it).</summary>
    public ContextMenu? Menu { get; private set; }

    /// <summary>The selection menu's model over the current selection, with the window's clipboard.</summary>
    public HexSelectionViewModel? CreateSelection() => Model is { } model ? new HexSelectionViewModel(model, Copy) : null;

    /// <summary>Builds and opens the selection menu at the pointer.</summary>
    public ContextMenu? OpenMenu()
    {
        if (CreateSelection() is not { } selection) return null;
        ContextMenu menu = InspectMenus.HexSelection(selection, ExportTarget);
        Menu = menu;
        menu.Open(View);
        return menu;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Model is not { } model) return;
        if (e.TryGetPosition(View.TextView, out Avalonia.Point point) && View.TryHitTest(point, out TapDirection direction, out long offset))
        {
            Selection selection = model.Sync.Selection;
            bool inside = selection.Kind == SelectionKind.ByteRange && selection.Direction == direction
                          && offset >= selection.Bytes.Offset && offset < selection.Bytes.End;
            if (!inside) View.SelectFieldAt(direction, offset);
        }

        OpenMenu();
        e.Handled = true;
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || CreateSelection() is not { HasBytes: true } selection) return;
        bool control = (e.KeyModifiers & KeyModifiers.Control) != 0;
        try
        {
            switch (e.Key)
            {
                case Key.C when control:
                    e.Handled = true;
                    await selection.CopyAsync(ByteCopyFormat.Hex).ConfigureAwait(true);
                    break;
                case Key.Home when !control:
                    e.Handled = selection.GoToFrameStart();
                    break;
                case Key.G when control:
                    e.Handled = true;
                    await selection.GoToOffsetAsync().ConfigureAwait(true);
                    break;
                case Key.B when control:
                    e.Handled = true;
                    selection.ToggleBookmark();
                    break;
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException or InvalidProgramException))
        {
            // A shortcut runs on the dispatcher: a failed copy must not end the app.
            System.Diagnostics.Trace.TraceError("hex shortcut failed: " + ex);
        }
    }

    private Task Copy(string text) =>
        TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : Task.CompletedTask;

    private async Task<Stream?> ExportTarget(string suggestedName)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return null;
        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export bytes",
            SuggestedFileName = suggestedName,
            DefaultExtension = "bin",
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        if (file is null) return null;
        using (file)
            return await file.OpenWriteAsync().ConfigureAwait(true);
    }
}
