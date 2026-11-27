using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace NexNet.Inspector.App.Views;

/// <summary>
/// What a view's menus need from its window: the clipboard, and a save picker for raw bytes. Both look the window up
/// when used, so a view made before it is shown still finds them; without a window (headless tests) they do nothing.
/// </summary>
public static class ViewServices
{
    /// <summary>Puts text on the clipboard of <paramref name="visual"/>'s window.</summary>
    public static Task Copy(Visual visual, string text) =>
        TopLevel.GetTopLevel(visual)?.Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : Task.CompletedTask;

    /// <summary>A copy function bound to <paramref name="visual"/>.</summary>
    public static Func<string, Task> CopyTo(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        return text => Copy(visual, text);
    }

    /// <summary>
    /// A save target bound to <paramref name="visual"/>'s window: the save picker with a suggested name, then the file
    /// opened for writing; null when canceled or there is no picker.
    /// </summary>
    public static Func<string, Task<Stream?>> SaveBytesTo(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        return async suggestedName =>
        {
            if (TopLevel.GetTopLevel(visual)?.StorageProvider is not { CanSave: true } storage) return null;
            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save bytes",
                SuggestedFileName = suggestedName,
                DefaultExtension = "bin",
                ShowOverwritePrompt = true,
            }).ConfigureAwait(true);
            if (file is null) return null;
            using (file)
                return await file.OpenWriteAsync().ConfigureAwait(true);
        };
    }

    /// <summary>Runs a menu or shortcut action on the dispatcher without letting a failure end the app.</summary>
    public static async void Run(Func<Task> action, string what)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or AccessViolationException or InvalidProgramException))
        {
            System.Diagnostics.Trace.TraceError(what + " failed: " + e);
        }
    }
}
