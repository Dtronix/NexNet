using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Model.Palette;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Shell;
using NexNet.Inspector.Core.Framing;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// Legend &amp; symbols (user report: "what is the T and N here?"): one table holds a line for every frame flag, every hex
/// fill and every symbol; F2, the app menu's View section and the palette open it as a modal dialog that Esc closes;
/// every line draws its swatch in both themes.
/// </summary>
[NotInParallel("avalonia")]
public sealed class LegendTests
{
    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow Window, MainWindowViewModel Model, ThemeService Themes) Open(AppTheme theme = AppTheme.Light)
    {
        ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), theme);
        MainWindowViewModel model = new(themes, null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        MainWindow window = new() { DataContext = model, Width = 1440, Height = 900 };
        window.Show();
        Settle();
        return (window, model, themes);
    }

    [Test]
    public async Task Every_frame_flag_has_a_line_whose_swatch_draws_its_chip()
    {
        foreach (FrameFlags flag in Enum.GetValues<FrameFlags>().Where(f => f != FrameFlags.None))
        {
            LegendSymbol symbol = Legend.For(flag);
            LegendEntry entry = Legend.Get(symbol);
            await Assert.That(entry.Section).IsEqualTo(LegendSection.FrameFlags);
            await Assert.That(Legend.FlagOf(symbol)).IsEqualTo(flag);
            await Assert.That(entry.Name).StartsWith(FlagChip.Letter(flag)).Because("the line names the chip's letter");
        }

        await Assert.That(Legend.Entries.Count(e => Legend.FlagOf(e.Symbol) is not null))
            .IsEqualTo(Enum.GetValues<FrameFlags>().Length - 1);
    }

    [Test]
    public async Task Every_hex_fill_has_a_line_whose_swatch_draws_that_fill()
    {
        foreach (HexCellRole role in Enum.GetValues<HexCellRole>())
        {
            LegendSymbol symbol = Legend.For(role);
            await Assert.That(Legend.Get(symbol).Section).IsEqualTo(LegendSection.Hex);
            await Assert.That(Legend.RoleOf(symbol)).IsEqualTo(role);
        }
    }

    [Test]
    public async Task Every_symbol_has_exactly_one_line_and_every_section_has_lines()
    {
        foreach (LegendSymbol symbol in Enum.GetValues<LegendSymbol>())
        {
            LegendEntry[] lines = Legend.Entries.Where(e => e.Symbol == symbol).ToArray();
            await Assert.That(lines.Length).IsEqualTo(1).Because(symbol.ToString());
            await Assert.That(lines[0].Name).IsNotEmpty();
            await Assert.That(lines[0].Text).IsNotEmpty();
        }

        foreach (LegendSection section in Enum.GetValues<LegendSection>())
        {
            await Assert.That(Legend.Entries.Any(e => e.Section == section)).IsTrue().Because(section.ToString());
            await Assert.That(Legend.Title(section)).IsNotEmpty();
        }

        // Sections read in order: a line never comes after a later section's line.
        LegendSection[] order = Legend.Entries.Select(e => e.Section).ToArray();
        await Assert.That(order.Zip(order.Skip(1)).All(p => p.First <= p.Second)).IsTrue();
    }

    [Test]
    public async Task The_columns_hold_every_section_once_in_order()
    {
        (LegendGroup[] left, LegendGroup[] right) = LegendDialogViewModel.Columns();
        string[] titles = [.. left.Concat(right).Select(g => g.Title)];
        await Assert.That(left.Length).IsGreaterThan(0);
        await Assert.That(right.Length).IsGreaterThan(0);
        await Assert.That(titles).IsEquivalentTo(Enum.GetValues<LegendSection>().Select(Legend.Title).ToArray(),
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(left.Concat(right).Sum(g => g.Entries.Count)).IsEqualTo(Legend.Entries.Count);
    }

    [Test]
    public async Task The_legend_command_is_F2_in_the_View_section_and_the_palette_lists_it()
    {
        CommandDefinition definition = CommandRegistry.Get(AppCommand.Legend);
        await Assert.That(definition.Title).IsEqualTo("Legend & symbols");
        await Assert.That(definition.Group).IsEqualTo(CommandGroup.View);
        await Assert.That(definition.ShortcutText).IsEqualTo("F2");
        await Assert.That(definition.IsWindowShortcut).IsTrue();

        CommandRegistry commands = new();
        commands.Bind(AppCommand.Legend, static () => { });
        CommandPaletteViewModel palette = new(new DocumentsViewModel(new Documents.TestUi(), null), commands);
        palette.Open("legend");
        PaletteRow[] rows = [.. palette.Rows.Where(r => r.IsItem && r.Group == PaletteGroup.Commands)];
        await Assert.That(rows.Length).IsGreaterThan(0);
        await Assert.That(rows[0].Command).IsEqualTo(AppCommand.Legend);
    }

    [Test]
    public async Task The_app_menu_item_opens_the_legend_and_Esc_closes_it()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open();
            try
            {
                model.IsAppMenuOpen = true;
                Settle();
                MenuItem item = window.AppMenu.LegendItem;
                string header = item.Header as string ?? "";
                string gesture = item.InputGesture?.ToString() ?? "";
                bool sameCommand = ReferenceEquals(item.Command, model.Commands.CommandFor(AppCommand.Legend));
                item.Command!.Execute(null);
                window.AppMenu.Close();
                Settle();
                bool opened = model.Dialog is LegendDialogViewModel;
                LegendDialog? view = window.GetVisualDescendants().OfType<LegendDialog>().FirstOrDefault(v => v.IsEffectivelyVisible);
                int swatches = view?.GetVisualDescendants().OfType<LegendSwatch>().Count() ?? 0;
                bool again = model.ShowLegend() is not null;
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Settle();
                bool closed = model.Dialog is null;

                window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
                Settle();
                bool byKey = model.Dialog is LegendDialogViewModel;
                return (header, gesture, sameCommand, opened, Shown: view is not null, swatches, again, closed, byKey);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.header).IsEqualTo("Legend & symbols");
            await Assert.That(seen.gesture).IsEqualTo("F2");
            await Assert.That(seen.sameCommand).IsTrue();
            await Assert.That(seen.opened).IsTrue();
            await Assert.That(seen.Shown).IsTrue();
            await Assert.That(seen.swatches).IsGreaterThan(0);
            await Assert.That(seen.again).IsFalse().Because("one dialog at a time");
            await Assert.That(seen.closed).IsTrue().Because("Esc closes the legend");
            await Assert.That(seen.byKey).IsTrue().Because("F2 opens it");
        }
    }

    [Test]
    [Arguments("Light")]
    [Arguments("Dark")]
    public async Task Every_line_draws_its_swatch(string variant)
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open(variant == "Dark" ? AppTheme.Dark : AppTheme.Light);
            try
            {
                model.ShowLegend();
                Settle();
                LegendDialog view = window.GetVisualDescendants().OfType<LegendDialog>().First();
                // Every line realised (the columns sit in a scroll viewer): scroll through it.
                ScrollViewer scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
                HashSet<LegendSymbol> drawn = [];
                for (double y = 0; ; y += 200)
                {
                    scroller.Offset = new Avalonia.Vector(0, y);
                    Settle();
                    foreach (LegendSwatch swatch in view.GetVisualDescendants().OfType<LegendSwatch>())
                    {
                        if (swatch.Bounds.Width >= LegendSwatch.SwatchWidth - 0.01 && swatch.Bounds.Height >= LegendSwatch.SwatchHeight - 0.01)
                            drawn.Add(swatch.Symbol);
                    }

                    if (y >= scroller.Extent.Height) break;
                }

                bool scrolls = scroller.Extent.Height > scroller.Viewport.Height;
                return (drawn, scrolls);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.scrolls).IsTrue();
        await Assert.That(Enum.GetValues<LegendSymbol>().Where(s => !seen.drawn.Contains(s)).ToArray()).IsEmpty();
    }
}
