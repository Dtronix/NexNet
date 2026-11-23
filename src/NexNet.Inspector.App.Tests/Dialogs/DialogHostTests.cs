using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls;

namespace NexNet.Inspector.App.Tests.Dialogs;

/// <summary>
/// The modal layer: initial focus, the focus trap (Tab cycles, focus that leaves comes back), Enter and Esc on the
/// frame, a scrim that never dismisses, the token scrim, the opacity fade, and focus handed back on close.
/// </summary>
[NotInParallel("avalonia")]
public sealed class DialogHostTests
{
    private sealed class Fixture
    {
        public required Window Window { get; init; }
        public required Button Invoker { get; init; }
        public required DialogHost Host { get; init; }
        public required DialogFrame Frame { get; init; }
        public required TextBox First { get; init; }
        public required TextBox Marked { get; init; }
        public int Primary { get; set; }
        public int Cancel { get; set; }

        public IInputElement? Focused => Window.FocusManager?.GetFocusedElement();

        public bool FocusInside => Focused is Visual v && Host.IsVisualAncestorOf(v);

        public void Key(Key key) => Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
    }

    private static Fixture Open()
    {
        Button invoker = new() { Content = "Connect…" };
        TextBox first = new() { Text = "first" };
        TextBox marked = new() { Text = "marked" };
        DialogHost.SetInitialFocus(marked, true);
        StackPanel body = new() { Spacing = 8 };
        body.Children.Add(first);
        body.Children.Add(marked);
        StackPanel footer = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(new Button { Content = "Cancel" });
        footer.Children.Add(new Button { Content = "OK" });
        DialogFrame frame = new() { Title = "Test dialog", Hint = "Enter · Esc", Width = 400, Content = body, Footer = footer };
        DialogHost host = new();
        Grid root = new() { RowDefinitions = new RowDefinitions("36,*") };
        root.Children.Add(invoker);
        Grid.SetRow(host, 1);
        root.Children.Add(host);
        Window window = new() { Width = 800, Height = 600, Content = root };
        Fixture f = new() { Window = window, Invoker = invoker, Host = host, Frame = frame, First = first, Marked = marked };
        frame.PrimaryCommand = new RelayCommand(() => f.Primary++);
        frame.CancelCommand = new RelayCommand(() => f.Cancel++);
        window.Show();
        UiPump.Frames();
        invoker.Focus(NavigationMethod.Tab);
        host.Dialog = frame;
        UiPump.Frames();
        return f;
    }

    [Test]
    public async Task Opening_focuses_the_marked_field_and_tab_stays_inside()
    {
        (bool open, bool markedFocused, int insideAfterTabs, int tabs, bool sawFirst) = await HeadlessSession.Run(() =>
        {
            Fixture f = Open();
            try
            {
                bool marked = ReferenceEquals(f.Focused, f.Marked);
                int inside = 0;
                bool sawFirst = false;
                const int presses = 8;
                for (int i = 0; i < presses; i++)
                {
                    f.Key(Key.Tab);
                    if (f.FocusInside) inside++;
                    sawFirst |= ReferenceEquals(f.Focused, f.First);
                }

                return (f.Host.IsOpen, marked, inside, presses, sawFirst);
            }
            finally
            {
                f.Window.Close();
            }
        });

        await Assert.That(open).IsTrue();
        await Assert.That(markedFocused).IsTrue();
        // Eight presses through five stops (✕, two fields, two buttons) wrap round, never leaving the dialog.
        await Assert.That(insideAfterTabs).IsEqualTo(tabs);
        await Assert.That(sawFirst).IsTrue();
    }

    [Test]
    public async Task Focus_that_escapes_is_sent_back_into_the_dialog()
    {
        (bool escaped, bool back) = await HeadlessSession.Run(() =>
        {
            Fixture f = Open();
            try
            {
                f.Invoker.Focus();
                bool escaped = ReferenceEquals(f.Focused, f.Invoker);
                UiPump.Frames();
                return (escaped, f.FocusInside);
            }
            finally
            {
                f.Window.Close();
            }
        });

        await Assert.That(escaped).IsTrue();
        await Assert.That(back).IsTrue();
    }

    [Test]
    public async Task Enter_runs_the_primary_command_and_Esc_the_cancel_command()
    {
        (int primary, int cancel, int primaryFromButton) = await HeadlessSession.Run(() =>
        {
            Fixture f = Open();
            try
            {
                f.First.Focus();
                f.Key(Key.Enter);
                f.Key(Key.Escape);
                // A focused button presses itself on Enter: the frame does not also run the primary command.
                int before = f.Primary;
                Button cancelButton = f.Frame.GetVisualDescendants().OfType<Button>().First(b => b.Content is "Cancel");
                cancelButton.Focus();
                f.Key(Key.Enter);
                return (before, f.Cancel, f.Primary - before);
            }
            finally
            {
                f.Window.Close();
            }
        });

        await Assert.That(primary).IsEqualTo(1);
        await Assert.That(cancel).IsEqualTo(1);
        await Assert.That(primaryFromButton).IsEqualTo(0);
    }

    [Test]
    public async Task A_press_on_the_scrim_does_not_dismiss()
    {
        (bool open, bool focusInside, int cancel) = await HeadlessSession.Run(() =>
        {
            Fixture f = Open();
            try
            {
                Point corner = new(4, f.Window.Bounds.Height - 4);
                f.Window.MouseDown(corner, MouseButton.Left, RawInputModifiers.None);
                f.Window.MouseUp(corner, MouseButton.Left, RawInputModifiers.None);
                UiPump.Frames();
                return (f.Host.IsOpen, f.FocusInside, f.Cancel);
            }
            finally
            {
                f.Window.Close();
            }
        });

        await Assert.That(open).IsTrue();
        await Assert.That(focusInside).IsTrue();
        await Assert.That(cancel).IsEqualTo(0);
    }

    [Test]
    [Arguments("Light", 0.45)]
    [Arguments("Dark", 0.72)]
    public async Task The_scrim_is_the_scrim_token_at_the_dialog_opacity(string theme, double opacity)
    {
        (Color? scrim, Color? token, double scrimOpacity, double hostOpacity) = await HeadlessSession.Run(() =>
            UiPump.InTheme(UiPump.Variant(theme), () =>
            {
                Fixture f = Open();
                try
                {
                    UiPump.Settle(300);
                    Border part = f.Host.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Scrim");
                    return (UiPump.ColorOf(part.Background), UiPump.Token("Brush.Overlay.Scrim", UiPump.Variant(theme)), part.Opacity, f.Host.Opacity);
                }
                finally
                {
                    f.Window.Close();
                }
            }));

        await Assert.That(scrim).IsNotNull();
        await Assert.That(scrim).IsEqualTo(token);
        await Assert.That(scrim!.Value.A).IsEqualTo((byte)255);
        await Assert.That(scrimOpacity).IsEqualTo(opacity);
        // Faded in by Opacity to fully shown.
        await Assert.That(hostOpacity).IsEqualTo(1.0);
    }

    [Test]
    public async Task Closing_fades_out_hands_focus_back_and_drops_the_dialog()
    {
        (bool openAfter, bool invokerFocused, bool stillShownDuringFade, bool visibleAfter, bool contentAfter, double opacity) = await HeadlessSession.Run(() =>
        {
            Fixture f = Open();
            try
            {
                f.Host.Dialog = null;
                bool focused = ReferenceEquals(f.Focused, f.Invoker);
                bool during = f.Host.IsVisible && f.Host.Content is not null;
                UiPump.Settle(DialogHost.FadeDuration.TotalMilliseconds + 200);
                return (f.Host.IsOpen, focused, during, f.Host.IsVisible, f.Host.Content is not null, f.Host.Opacity);
            }
            finally
            {
                f.Window.Close();
            }
        });

        await Assert.That(openAfter).IsFalse();
        await Assert.That(invokerFocused).IsTrue();
        await Assert.That(stillShownDuringFade).IsTrue();
        await Assert.That(visibleAfter).IsFalse();
        await Assert.That(contentAfter).IsFalse();
        await Assert.That(opacity).IsEqualTo(0.0);
    }
}
