using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Tests.Capture;

namespace NexNet.Inspector.App.Tests.Dialogs;

/// <summary>The banner's three kinds and the status line's tones (OQ2), and the gallery sections that show them.</summary>
[NotInParallel("avalonia")]
public sealed class BannerTests
{
    private readonly record struct Drawn(bool Dot, bool Spinner, bool Badge, Color? DotColour, Color? Fill, double Height, AutomationLiveSetting Live);

    private static Drawn Render(Banner banner)
    {
        Window window = new() { Width = 900, Height = 200, Content = banner };
        window.Show();
        try
        {
            UiPump.Frames();
            Ellipse dot = banner.GetVisualDescendants().OfType<Ellipse>().First(e => e.Name == "PART_Dot");
            Panel spinner = banner.GetVisualDescendants().OfType<Panel>().First(p => p.Name == "PART_Spinner");
            Border badge = banner.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Badge");
            Border root = banner.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Root");
            return new Drawn(dot.IsEffectivelyVisible, spinner.IsEffectivelyVisible, badge.IsEffectivelyVisible, UiPump.ColorOf(dot.Fill),
                UiPump.ColorOf(root.Background), banner.Bounds.Height, AutomationProperties.GetLiveSetting(banner));
        }
        finally
        {
            window.Close();
        }
    }

    [Test]
    public async Task A_status_line_shows_a_dot_per_tone_and_a_spinner_while_busy()
    {
        (Drawn ok, Drawn busy, Drawn error, Color? okToken, Color? errorToken, Color? raised) = await HeadlessSession.Run(() => (
            Render(new Banner { Tone = StatusTone.Ok, Content = "Reached" }),
            Render(new Banner { Tone = StatusTone.Busy, Content = "Connecting…" }),
            Render(new Banner { Tone = StatusTone.Error, Content = "Refused" }),
            UiPump.Token("Brush.Status.Ok", ThemeVariant.Light),
            UiPump.Token("Brush.Status.Error", ThemeVariant.Light),
            UiPump.Token("Brush.Surface.Raised", ThemeVariant.Light)));

        await Assert.That(ok.Dot).IsTrue();
        await Assert.That(ok.DotColour).IsEqualTo(okToken);
        await Assert.That(ok.Fill).IsEqualTo(raised);
        await Assert.That(ok.Live).IsEqualTo(AutomationLiveSetting.Polite);
        await Assert.That(busy.Dot).IsFalse();
        await Assert.That(busy.Spinner).IsTrue();
        await Assert.That(error.DotColour).IsEqualTo(errorToken);
        await Assert.That(error.Badge).IsFalse();
    }

    [Test]
    public async Task The_session_ended_strip_and_the_schema_card_take_the_warning_surface()
    {
        (Drawn strip, Drawn card, Color? warning) = await HeadlessSession.Run(() => (
            Render(new Banner { Kind = BannerKind.SessionEnded, Title = "Session 0x3A21 ended", Content = "not saved", Actions = new Button { Content = "Discard" } }),
            Render(new Banner { Kind = BannerKind.SchemaMismatch, Title = "The embedded schema differs", Content = "hashes" }),
            UiPump.Token("Brush.Warning.Surface", ThemeVariant.Light)));

        await Assert.That(strip.Height).IsEqualTo(34);
        await Assert.That(strip.Fill).IsEqualTo(warning);
        await Assert.That(strip.Live).IsEqualTo(AutomationLiveSetting.Assertive);
        await Assert.That(card.Badge).IsTrue();
        await Assert.That(card.Dot).IsFalse();
        await Assert.That(card.Fill).IsEqualTo(warning);
    }

    [Test]
    public async Task The_gallery_has_card_banner_and_dialog_sections_that_build()
    {
        string[] titles = [.. ControlGallery.Sections.Select(s => s.Title)];
        int built = await HeadlessSession.Run(() =>
        {
            int count = 0;
            foreach (GallerySection section in HomeGallery.Sections())
            {
                Window window = new() { Width = 1440, Height = 1600, Content = section.Build() };
                window.Show();
                UiPump.Frames();
                window.Close();
                count++;
            }

            return count;
        });

        await Assert.That(titles).Contains("Card");
        await Assert.That(titles).Contains("Banner");
        await Assert.That(titles).Contains("Dialog");
        await Assert.That(built).IsEqualTo(3);
    }
}
