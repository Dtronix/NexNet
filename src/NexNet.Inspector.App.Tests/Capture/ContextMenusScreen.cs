using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Inspect;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The context-menus screen (PLAN §8): inspect-live with three menus open, built by the app's own menu builders over
/// the live models: the lane menu on the FileUpload pipe ("Trace frames in view" hovered, "Close pipe in view" disabled
/// because the pipe is still open in view: the window ends just before it closes), the hex selection menu on 16 bytes
/// of the traced chunk, and its "Decode selection as ▸" submenu open. Popups are separate windows that a window capture
/// does not hold, so the menus are hosted inline on a canvas over the window, where the pointer would open them.
/// </summary>
public static class ContextMenusScreen
{
    /// <summary>Where the menus sit, from the package's screen (1440 px wide).</summary>
    private const double LaneMenuLeft = 121;
    private const double HexMenuLeft = 1012;
    private const double HexMenuTop = 472;

    /// <summary>The parent item's top inside the hex menu: border, padding, header, separator, three items, separator.</summary>
    private const double DecodeItemTop = 1 + 4 + 24 + 7 + (3 * 26) + 7;

    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        InspectLiveScreen.Picks picks = InspectLiveScreen.Open(viewModel);
        SyncBus sync = viewModel.Documents.Sync;
        LaneKey pipe = new(LaneKind.Pipe, picks.Pipe);
        TimelineModel timeline = viewModel.Inspect.Timeline.Model;

        // The package draws the pipe live ("raw"): end the window a millisecond before the fixture's pipe closes.
        if (timeline.FindLane(pipe) is { LifeEnd: not long.MaxValue } lane)
        {
            long ms = picks.Document.Session!.Header.StopwatchFrequency / 1000;
            long over = sync.Window.End - (lane.LifeEnd - ms);
            if (over > 0) sync.SetWindow(sync.Window.Pan(-over), byUser: false);
        }

        // 16 bytes of the traced chunk from its type byte, selected in the hex view (the message stays traced).
        Core.Session.CaptureSession session = picks.Document.Session!;
        Core.Framing.FrameRecord chunk;
        lock (session.Gate) session.TryGetFrame(picks.Chunk, out chunk);
        sync.Select(Selection.OfBytes(picks.Chunk.Direction, new Core.Assembly.StreamRange(chunk.Offset, 16))
            with { Frame = picks.Chunk, Message = picks.Message }, chunk.StartTicks);

        viewModel.Inspect.Right.View.Value = StreamPaneKind.Hex;
        InspectLiveScreen.Pump(static () => true);

        MainWindow window = new() { DataContext = viewModel };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        window.Opened += (_, _) => Place(window, viewModel, pipe);
        return window;
    }

    private static void Place(MainWindow window, MainWindowViewModel viewModel, LaneKey pipe)
    {
        window.UpdateLayout();
        Canvas overlay = new() { IsHitTestVisible = false };
        Grid.SetRowSpan(overlay, 2);
        window.Root.Children.Add(overlay);

        // The lane menu, opened at the pointer on the pipe's gutter (just under the lane).
        InspectView inspect = window.Inspect;
        TimelineControl timeline = inspect.Timeline;
        LayoutRow row = timeline.Layout.Rows.ToArray().FirstOrDefault(r => r.Lane?.Key == pipe);
        double laneBottom = timeline.LanesTop + row.Bottom - timeline.LaneScroll;
        Point at = timeline.TranslatePoint(new Point(LaneMenuLeft, laneBottom), window.Root) ?? new Point(LaneMenuLeft, 248);
        if (inspect.CreateLaneMenu(pipe) is { } laneModel)
        {
            ContextMenu lane = InspectMenus.Lane(laneModel);
            Set(Item(lane, "Trace frames in view"), ":pointerover");
            Add(overlay, lane, at.X, at.Y);
        }

        // The hex selection menu and its open submenu.
        HexPane? pane = inspect.GetVisualDescendants<HexPane>();
        if (pane?.CreateSelection() is not { } selection) return;
        ContextMenu hex = InspectMenus.HexSelection(selection, static _ => Task.FromResult<Stream?>(null));
        MenuItem decode = Item(hex, "Decode selection as");
        Set(decode, ":selected");
        Set(decode, ":open");
        Add(overlay, hex, HexMenuLeft, HexMenuTop);
        window.UpdateLayout();

        ContextMenu submenu = new();
        foreach (Control item in InspectMenus.DecodeItems(submenu, selection)) submenu.Items.Add(item);
        // As the theme's popup places it: to the right of the parent (overlapping by its 1 px border), the first item level with "Decode selection as".
        Add(overlay, submenu, HexMenuLeft + hex.Bounds.Width - 1, HexMenuTop + DecodeItemTop - 5);
    }

    private static void Add(Canvas overlay, ContextMenu menu, double left, double top)
    {
        menu.HorizontalAlignment = HorizontalAlignment.Left;
        menu.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(menu, left);
        Canvas.SetTop(menu, top);
        overlay.Children.Add(menu);
    }

    private static MenuItem Item(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().First(m => m.Header as string == header);

    private static void Set(MenuItem item, string pseudo) => ((IPseudoClasses)item.Classes).Set(pseudo, true);

    private static T? GetVisualDescendants<T>(this Visual root) where T : Visual =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>().FirstOrDefault();
}
