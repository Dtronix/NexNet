using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Home;

/// <summary>
/// The empty window's view: a .nntl dragged over it lights the drop card, dropping it opens the file, and the
/// "Open a recording" card asks the window's storage provider for a file.
/// </summary>
public sealed partial class HomeView : UserControl
{
    private static readonly FilePickerFileType Recordings = new("NexNet recordings") { Patterns = ["*" + HomeViewModel.RecordingExtension] };

    public HomeView()
    {
        InitializeComponent();
        DragDrop.AddDragEnterHandler(this, OnDragEnter);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDragLeaveHandler(this, OnDragLeave);
        DragDrop.AddDropHandler(this, OnDrop);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is HomeViewModel home) home.PickFile ??= PickRecordingAsync;
        };
    }

    private HomeViewModel? Home => DataContext as HomeViewModel;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        bool accepted = Home?.DragEnter(Paths(e)) == true;
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = Home?.IsDragOver == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => Home?.DragLeave();

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Home is not { } home) return;
        try
        {
            await home.DropAsync(Paths(e));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // An async void handler must not throw into the dispatcher; the home already reports file errors.
            System.Diagnostics.Debug.WriteLine("drop failed: " + ex.Message);
        }
    }

    /// <summary>The local paths of the dragged files (a dragged URL or text yields none).</summary>
    private static List<string> Paths(DragEventArgs e)
    {
        List<string> paths = [];
        if (e.DataTransfer.TryGetFiles() is not { } files) return paths;
        foreach (IStorageItem item in files)
        {
            if (item.TryGetLocalPath() is { } path) paths.Add(path);
        }

        return paths;
    }

    private async Task<string?> PickRecordingAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return null;
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a recording",
            AllowMultiple = false,
            FileTypeFilter = [Recordings],
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
