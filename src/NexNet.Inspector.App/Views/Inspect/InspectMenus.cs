using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Inspect;

/// <summary>
/// The Inspect workspace's context menus (screen context-menus), drawn by the app-wide menu theme (Themes/Menus.axaml):
/// the timeline's lane menu, its group-header menu, and the hex view's selection menu with its "Decode selection as ▸"
/// submenu. Each is built when it opens from a menu view-model made at that moment, so its checks and disabled items
/// say what is true then.
/// </summary>
public static class InspectMenus
{
    /// <summary>The lane menu: header, Pin / Expand / Hide, Trace / Select all / Show in, Compare / Copy name, Close.</summary>
    public static ContextMenu Lane(LaneMenuViewModel lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ContextMenu menu = new();
        menu.Items.Add(Header(GlyphOf(lane.Glyph), lane.Name, lane.Detail, monoDetail: false));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(menu, "Pin lane", new KeyGesture(Key.P), lane.TogglePin, check: lane.IsPinned));
        menu.Items.Add(Item(menu, "Expand lane", new KeyGesture(Key.Space), lane.ToggleExpanded, check: lane.IsExpanded));
        menu.Items.Add(Item(menu, "Hide lane", new KeyGesture(Key.H), lane.Hide));
        menu.Items.Add(new Separator());
        bool canTrace = lane.CanTrace;
        menu.Items.Add(Item(menu, "Trace frames in view", new KeyGesture(Key.T), () => lane.Trace(), icon: "⌖", enabled: canTrace));
        menu.Items.Add(Item(menu, "Select all frames", new KeyGesture(Key.A, KeyModifiers.Control), static () => { }, enabled: lane.CanSelectAll));
        menu.Items.Add(Item(menu, "Show in Ladder", null, () => lane.ShowIn(StreamPaneKind.Ladder), enabled: canTrace));
        menu.Items.Add(Item(menu, "Show in Hex stream", null, () => lane.ShowIn(StreamPaneKind.Hex), enabled: canTrace));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(menu, "Compare this pipe across files…", null, static () => { }, icon: "◫", enabled: lane.CanCompare));
        menu.Items.Add(Item(menu, "Copy name", new KeyGesture(Key.C, KeyModifiers.Control), lane.CopyNameAsync, enabled: lane.CanCopy));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(menu, "Close pipe in view", null, lane.CloseInView, enabled: lane.CanClose));
        return menu;
    }

    /// <summary>A group header's menu: collapse or expand the group, and show the lanes hidden from it.</summary>
    public static ContextMenu Group(TimelineModel model, LaneGroupKind group)
    {
        ArgumentNullException.ThrowIfNull(model);
        ContextMenu menu = new();
        menu.Items.Add(Header(null, TimelineModel.Title(group), null, monoDetail: false));
        menu.Items.Add(new Separator());
        bool collapsed = model.IsCollapsed(group);
        menu.Items.Add(Item(menu, collapsed ? "Expand group" : "Collapse group", null, () => model.ToggleCollapsed(group)));
        int hidden = model.HiddenCount(group);
        string label = hidden == 0 ? "Show hidden lanes" : "Show hidden lanes (" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        menu.Items.Add(Item(menu, label, null, () => model.ShowHidden(group), enabled: hidden > 0));
        return menu;
    }

    /// <summary>
    /// The hex selection menu: header, Copy as hex / C# byte array / Base64, Decode selection as ▸, Go to frame start,
    /// Go to offset…, Bookmark offset, Export bytes….
    /// </summary>
    /// <param name="selection">The selection's menu model.</param>
    /// <param name="exportTarget">Asks where to export (a save picker), given a suggested file name; null disables Export.</param>
    public static ContextMenu HexSelection(HexSelectionViewModel selection, Func<string, Task<Stream?>>? exportTarget)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ContextMenu menu = new();
        bool has = selection.HasBytes;
        bool canCopy = selection.CanCopy;
        menu.Items.Add(Header(null, selection.CountText, selection.RangeText, monoDetail: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(menu, "Copy as hex", new KeyGesture(Key.C, KeyModifiers.Control), () => selection.CopyAsync(ByteCopyFormat.Hex), enabled: canCopy));
        menu.Items.Add(Item(menu, "Copy as C# byte array", null, () => selection.CopyAsync(ByteCopyFormat.CSharp), enabled: canCopy));
        menu.Items.Add(Item(menu, "Copy as Base64", null, () => selection.CopyAsync(ByteCopyFormat.Base64), enabled: canCopy));
        menu.Items.Add(new Separator());
        MenuItem decode = new() { Header = "Decode selection as", IsEnabled = has };
        foreach (Control item in DecodeItems(menu, selection)) decode.Items.Add(item);
        menu.Items.Add(decode);
        menu.Items.Add(Item(menu, "Go to frame start", new KeyGesture(Key.Home), () => selection.GoToFrameStart(), enabled: selection.CanGoToFrameStart));
        menu.Items.Add(Item(menu, "Go to offset…", new KeyGesture(Key.G, KeyModifiers.Control), selection.GoToOffsetAsync, enabled: selection.CanGoToOffset));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(menu, selection.IsBookmarked ? "Remove bookmark" : "Bookmark offset", new KeyGesture(Key.B, KeyModifiers.Control),
            () => selection.ToggleBookmark(), enabled: has));
        menu.Items.Add(Item(menu, "Export bytes…", null, () => ExportAsync(selection, exportTarget!),
            enabled: exportTarget is not null && selection.CanRead));
        return menu;
    }

    /// <summary>
    /// The "Decode selection as ▸" submenu's items: Auto (schema), MessagePack value, UTF-8 text, LEB128 length + item,
    /// then Raw only, the reading shown checked.
    /// </summary>
    /// <param name="owner">The menu to close when one is picked.</param>
    /// <param name="selection">The selection's menu model.</param>
    public static IReadOnlyList<Control> DecodeItems(ContextMenu owner, HexSelectionViewModel selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        DecodeAs current = selection.Decode;
        MenuItem Mode(string header, DecodeAs mode) => Item(owner, header, null, () => selection.DecodeSelectionAs(mode), check: current == mode);
        return
        [
            Mode("Auto (schema)", DecodeAs.Auto),
            Mode("MessagePack value", DecodeAs.MessagePack),
            Mode("UTF-8 text", DecodeAs.Utf8),
            Mode("LEB128 length + item", DecodeAs.Leb128),
            new Separator(),
            Mode("Raw only", DecodeAs.Raw),
        ];
    }

    /// <summary>The glyph a lane's header starts with (the timeline gutter's).</summary>
    public static string? GlyphOf(LaneGlyph glyph) => glyph switch
    {
        LaneGlyph.Inbound => "▼",
        LaneGlyph.Outbound => "▲",
        LaneGlyph.Pipe => PipeText.RawGlyph,
        LaneGlyph.Channel => PipeText.ChannelGlyph,
        LaneGlyph.Collection => PipeText.CollectionGlyph,
        LaneGlyph.Session => "◆",
        _ => null,
    };

    /// <summary>
    /// The context header: a glyph in direction.inbound, the target's name in bold primary, then the secondary detail
    /// (mono for offsets).
    /// </summary>
    public static MenuItem Header(string? glyph, string name, string? detail, bool monoDetail)
    {
        TextBlock text = new();
        if (glyph is not null)
        {
            Run g = new(glyph + "  ");
            g.Bind(TextElement.ForegroundProperty, text.GetResourceObservable("Brush.Direction.Inbound"));
            text.Inlines!.Add(g);
        }

        Run n = new(name) { FontWeight = FontWeight.Bold };
        n.Bind(TextElement.ForegroundProperty, text.GetResourceObservable("Brush.Text.Primary"));
        text.Inlines!.Add(n);
        if (!string.IsNullOrEmpty(detail))
        {
            Run d = new("  " + detail);
            if (monoDetail) d.Bind(TextElement.FontFamilyProperty, text.GetResourceObservable("Font.Mono"));
            text.Inlines!.Add(d);
        }

        return new MenuItem { Header = text, Classes = { "header" } };
    }

    internal static MenuItem Item(ContextMenu owner, string header, KeyGesture? gesture, Action run, bool? check = null, string? icon = null, bool enabled = true) =>
        Item(owner, header, gesture, () =>
        {
            run();
            return Task.CompletedTask;
        }, check, icon, enabled);

    internal static MenuItem Item(ContextMenu owner, string header, KeyGesture? gesture, Func<Task> run, bool? check = null, string? icon = null, bool enabled = true)
    {
        MenuItem item = new() { Header = header, InputGesture = gesture, IsEnabled = enabled };
        if (check is { } c)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = c;
        }

        if (icon is not null) item.Icon = new TextBlock { Text = icon };
        item.Click += async (_, _) =>
        {
            owner.Close();
            try
            {
                await run().ConfigureAwait(true);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or AccessViolationException or InvalidProgramException))
            {
                // A menu click runs on the dispatcher: a failed copy or export must not end the app.
                Trace.TraceError("menu: " + header + " failed: " + e);
            }
        };
        return item;
    }

    private static async Task ExportAsync(HexSelectionViewModel selection, Func<string, Task<Stream?>> target)
    {
        Stream? stream = await target(selection.ExportName).ConfigureAwait(true);
        if (stream is null) return;
        await using (stream.ConfigureAwait(true))
            await selection.ExportAsync(stream).ConfigureAwait(true);
    }
}
