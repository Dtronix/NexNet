using Avalonia.Controls;
using Avalonia.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Inspect;

/// <summary>
/// The content menu every content view opens on a right-click (Events, Ladder, Message trace, the property trees,
/// Compare's field diff and entity sidebar, the session list, timeline marks), drawn by the app-wide menu theme like
/// I26's menus: a header naming the item, then the text items (Copy Ctrl+C, Copy value, Copy path, Copy row as TSV) and,
/// when the item maps to bytes, the binary ones (Copy as hex / C# byte array / Base64, Save bytes to file… — for a frame
/// of a reassembled message also Save message body… — and Open as ▸), then Export section as .nntl… where offered.
/// </summary>
public static class ContentMenus
{
    /// <summary>Builds the menu for <paramref name="model"/>.</summary>
    /// <param name="model">The item and what can be done with it.</param>
    /// <param name="exportSection">Opens Export section as .nntl… on the item's range; null leaves the item out.</param>
    public static ContextMenu Build(ContentMenuViewModel model, Action? exportSection = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ContextMenu menu = new();
        Add(menu, model, exportSection, header: true);
        return menu;
    }

    /// <summary>Appends the item's entries to an existing menu (the timeline's lane menu over a mark).</summary>
    public static void Append(ContextMenu menu, ContentMenuViewModel model)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(model);
        menu.Items.Add(new Separator());
        Add(menu, model, null, header: true);
    }

    private static void Add(ContextMenu menu, ContentMenuViewModel model, Action? exportSection, bool header)
    {
        var item = model.Item;
        bool copy = model.CanCopy;
        if (header)
        {
            menu.Items.Add(InspectMenus.Header(null, item.Title, item.Detail, monoDetail: false));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(InspectMenus.Item(menu, "Copy", new KeyGesture(Key.C, KeyModifiers.Control), model.CopyTextAsync, enabled: copy));
        if (item.Value is not null)
            menu.Items.Add(InspectMenus.Item(menu, "Copy value", null, model.CopyValueAsync, enabled: copy));
        if (item.Path is not null)
            menu.Items.Add(InspectMenus.Item(menu, "Copy path", null, model.CopyPathAsync, enabled: copy));
        if (item.Row is not null)
            menu.Items.Add(InspectMenus.Item(menu, "Copy row as TSV", null, model.CopyRowAsync, enabled: copy));

        if (model.HasBytes)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(InspectMenus.Item(menu, "Copy as hex", null, () => model.CopyBytesAsync(ByteCopyFormat.Hex), enabled: copy));
            menu.Items.Add(InspectMenus.Item(menu, "Copy as C# byte array", null, () => model.CopyBytesAsync(ByteCopyFormat.CSharp), enabled: copy));
            menu.Items.Add(InspectMenus.Item(menu, "Copy as Base64", null, () => model.CopyBytesAsync(ByteCopyFormat.Base64), enabled: copy));
            if (model.HasMessageBody)
            {
                menu.Items.Add(InspectMenus.Item(menu, "Save frame bytes to file…", null, () => model.SaveAsync(ContentByteSource.Item), enabled: model.CanSave));
                menu.Items.Add(InspectMenus.Item(menu, "Save message body to file…", null, () => model.SaveAsync(ContentByteSource.MessageBody),
                    enabled: model.CanSave));
            }
            else
            {
                menu.Items.Add(InspectMenus.Item(menu, "Save bytes to file…", null, () => model.SaveAsync(ContentByteSource.Item), enabled: model.CanSave));
            }

            MenuItem openAs = new() { Header = "Open as", IsEnabled = model.CanOpenAs };
            foreach (Control entry in OpenAsItems(menu, model)) openAs.Items.Add(entry);
            menu.Items.Add(openAs);
        }

        if (exportSection is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(InspectMenus.Item(menu, "Export section as .nntl…", new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift),
                exportSection, icon: null));
        }
    }

    /// <summary>"Open as ▸": MessagePack value, UTF-8 text, LEB128 length + item, then Raw only.</summary>
    public static IReadOnlyList<Control> OpenAsItems(ContextMenu owner, ContentMenuViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        MenuItem Mode(string header, DecodeAs mode) => InspectMenus.Item(owner, header, null, () => model.OpenAs(mode));
        return
        [
            Mode("MessagePack value", DecodeAs.MessagePack),
            Mode("UTF-8 text", DecodeAs.Utf8),
            Mode("LEB128 length + item", DecodeAs.Leb128),
            new Separator(),
            Mode("Raw only", DecodeAs.Raw),
        ];
    }
}
