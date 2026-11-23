using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls;

namespace NexNet.Inspector.App.Tests.Inspector;

/// <summary>The pane header (controls/pane-header): its variants, the synced badge and the split button.</summary>
[NotInParallel("avalonia")]
public sealed class PaneHeaderTests
{
    private static readonly string[] Tabs = ["Events", "Ladder", "Hex stream"];

    private static T Part<T>(PaneHeader header, string name)
        where T : Control => header.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static (Window Window, PaneHeader Header) Show(PaneHeader header)
    {
        Window window = new() { Content = header, Width = 700, Height = 60 };
        window.Show();
        UiPump.Frames();
        return (window, header);
    }

    [Test]
    public async Task The_tabbed_header_shows_its_tabs_and_selects_by_index()
    {
        var (window, header) = await HeadlessSession.Run(() => Show(new PaneHeader { Tabs = Tabs, SelectedIndex = 1 }));
        try
        {
            var (count, selected, titleVisible, height) = await HeadlessSession.Run(() =>
            {
                ListBox tabs = Part<ListBox>(header, "PART_Tabs");
                tabs.SelectedIndex = 2;
                UiPump.Frames();
                return (tabs.ItemCount, header.SelectedIndex, Part<StackPanel>(header, "PART_Title").IsVisible, header.Bounds.Height);
            });

            await Assert.That(count).IsEqualTo(3);
            await Assert.That(selected).IsEqualTo(2);
            await Assert.That(titleVisible).IsFalse();
            await Assert.That(height).IsEqualTo(24);
        }
        finally
        {
            await HeadlessSession.Run(window.Close);
        }
    }

    [Test]
    public async Task The_titled_header_shows_a_title_instead_of_tabs()
    {
        var (window, header) = await HeadlessSession.Run(() => Show(new PaneHeader { Variant = PaneHeaderVariant.Titled, Title = "Field diff", Subtitle = "#812" }));
        try
        {
            var (tabs, title) = await HeadlessSession.Run(() => (Part<ListBox>(header, "PART_Tabs").IsVisible, Part<StackPanel>(header, "PART_Title").IsVisible));
            await Assert.That(tabs).IsFalse();
            await Assert.That(title).IsTrue();
        }
        finally
        {
            await HeadlessSession.Run(window.Close);
        }
    }

    [Test]
    public async Task The_badge_reads_synced_unsynced_or_nothing()
    {
        var (window, header) = await HeadlessSession.Run(() => Show(new PaneHeader { Tabs = Tabs }));
        try
        {
            (bool Synced, bool Unsynced) Badge() => (Part<StackPanel>(header, "PART_Synced").IsVisible, Part<TextBlock>(header, "PART_Unsynced").IsVisible);

            var none = await HeadlessSession.Run(Badge);
            var synced = await HeadlessSession.Run(() =>
            {
                header.SyncState = PaneSyncState.Synced;
                return Badge();
            });
            var unsynced = await HeadlessSession.Run(() =>
            {
                header.SyncState = PaneSyncState.Unsynced;
                return Badge();
            });

            await Assert.That(none).IsEqualTo((false, false));
            await Assert.That(synced).IsEqualTo((true, false));
            await Assert.That(unsynced).IsEqualTo((false, true));
        }
        finally
        {
            await HeadlessSession.Run(window.Close);
        }
    }

    [Test]
    public async Task The_split_button_reads_Single_Split_or_close_and_runs_its_command()
    {
        int runs = 0;
        RelayCommand command = new(() => runs++);
        var (window, header) = await HeadlessSession.Run(() => Show(new PaneHeader { Tabs = Tabs, SplitCommand = command }));
        try
        {
            (bool Visible, string Text, bool Glyph, string? Name, bool Enabled) Split()
            {
                UiPump.Frames();
                Button button = Part<Button>(header, "PART_Split");
                return (button.IsVisible, Part<TextBlock>(header, "PART_SplitText").Text ?? "", Part<PathIcon>(header, "PART_CloseGlyph").IsVisible,
                    AutomationProperties.GetName(button), button.IsEffectivelyEnabled);
            }

            bool none = await HeadlessSession.Run(() => Part<Button>(header, "PART_Split").IsVisible);
            var single = await HeadlessSession.Run(() =>
            {
                header.SplitAction = PaneSplitAction.Single;
                return Split();
            });
            var split = await HeadlessSession.Run(() =>
            {
                header.SplitAction = PaneSplitAction.Split;
                return Split();
            });
            var close = await HeadlessSession.Run(() =>
            {
                header.SplitAction = PaneSplitAction.Close;
                return Split();
            });
            await HeadlessSession.Run(() => Part<Button>(header, "PART_Split").Command!.Execute(null));
            var disabled = await HeadlessSession.Run(() =>
            {
                header.SplitAction = PaneSplitAction.Split;
                header.CanSplit = false;
                return Split();
            });

            await Assert.That(none).IsFalse();
            await Assert.That(single).IsEqualTo((true, "Single", false, "Toggle split view", true));
            await Assert.That(split.Text).IsEqualTo("Split");
            await Assert.That(close.Glyph).IsTrue();
            await Assert.That(close.Name).IsEqualTo("Close pane");
            await Assert.That(runs).IsEqualTo(1);
            await Assert.That(disabled.Enabled).IsFalse();
        }
        finally
        {
            await HeadlessSession.Run(window.Close);
        }
    }
}
