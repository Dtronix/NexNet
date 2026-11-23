using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;

namespace NexNet.Inspector.App.Tests;

/// <summary>
/// The app tooltip (OQ9) is the menu surface: surface.panel in a 1 px border.strong outline, 8 px corners, shadow.menu, 11 px
/// primary Font.Ui text that wraps at 320 px, shown after a 400 ms rest. The live App, headless, in both themes.
/// </summary>
[NotInParallel("avalonia")]   // the headless session and Application.Current's theme are process-wide
public sealed class ToolTipThemeTests
{
    private sealed record Seen(ToolTip Tip, Border? Chrome, Button Anchor);

    /// <summary>Opens a tooltip on a button in a live window in <paramref name="theme"/> and hands it to <paramref name="body"/>.</summary>
    private static Task<T> WithTip<T>(ThemeVariant theme, string text, Func<Seen, T> body) => HeadlessSession.Run(() => UiPump.InTheme(theme, () =>
    {
        ToolTip tip = new() { Content = text };
        Button anchor = new() { Content = "Record", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(anchor, tip);
        Window window = new() { Width = 600, Height = 400, Content = anchor };
        window.Show();
        try
        {
            UiPump.Frames();
            ToolTip.SetIsOpen(anchor, true);
            UiPump.Frames();
            if (tip.Bounds.Width <= 0) throw new InvalidOperationException("the tooltip did not open and lay out");
            return body(new Seen(tip, tip.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Chrome"), anchor));
        }
        finally
        {
            ToolTip.SetIsOpen(anchor, false);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }));

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task A_tooltip_is_the_menu_surface_with_11_px_primary_text(string variant)
    {
        ThemeVariant theme = UiPump.Variant(variant);
        List<string> wrong = await WithTip(theme, "Recording disabled by server config", s =>
        {
            List<string> w = [];
            ToolTip t = s.Tip;
            if (UiPump.ColorOf(t.Background) != UiPump.Token("Brush.Surface.Panel", theme)) w.Add($"background {UiPump.ColorOf(t.Background)} is not surface.panel");
            if (UiPump.ColorOf(t.BorderBrush) != UiPump.Token("Brush.Border.Strong", theme)) w.Add($"border {UiPump.ColorOf(t.BorderBrush)} is not border.strong");
            if (t.BorderThickness != new Thickness(1)) w.Add($"border thickness {t.BorderThickness}");
            if (t.CornerRadius != new CornerRadius(8)) w.Add($"corner radius {t.CornerRadius}");
            if (UiPump.ColorOf(t.Foreground) != UiPump.Token("Brush.Text.Primary", theme)) w.Add($"text {UiPump.ColorOf(t.Foreground)} is not text.primary");
            if (t.FontSize != 11) w.Add($"text size {t.FontSize}");
            if (t.FontFamily != (UiPump.Resource("Font.Ui", theme) as FontFamily)) w.Add($"family {t.FontFamily}");
            if (s.Chrome is not { } chrome) w.Add("the template has no PART_Chrome");
            else
            {
                if (UiPump.ColorOf(chrome.Background) != UiPump.ColorOf(t.Background)) w.Add("the template does not draw the background");
                if (!Equals(chrome.BoxShadow, UiPump.Resource("Shadow.Menu", theme))) w.Add($"shadow {chrome.BoxShadow} is not shadow.menu");
            }

            TextBlock? text = s.Tip.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            if (text is null) w.Add("no text drawn");
            else if (UiPump.ColorOf(text.Foreground) != UiPump.Token("Brush.Text.Primary", theme)) w.Add("the drawn text is not text.primary");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    public async Task A_long_tooltip_wraps_at_320_px()
    {
        (double width, double height) = await WithTip(ThemeVariant.Dark, string.Join(' ', Enumerable.Repeat("Recording disabled by server config.", 8)),
            s => (s.Tip.Bounds.Width, s.Tip.Bounds.Height));
        using (Assert.Multiple())
        {
            await Assert.That(width).IsLessThanOrEqualTo(MenuMetrics.ToolTipMaxWidth + 0.5);
            await Assert.That(height).IsGreaterThan(40).Because("the text wraps onto several lines");
        }
    }

    [Test]
    public async Task A_control_in_a_window_waits_400_ms_before_its_tooltip()
    {
        int delay = await WithTip(ThemeVariant.Dark, "tip", s => ToolTip.GetShowDelay(s.Anchor));
        await Assert.That(delay).IsEqualTo(MenuMetrics.ToolTipDelay).Because("OQ9: 400 ms, Avalonia's default");
    }
}
