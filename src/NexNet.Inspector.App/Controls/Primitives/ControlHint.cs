using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace NexNet.Inspector.App.Controls.Primitives;

/// <summary>
/// The secondary line or trailing hint of an option control: a checkbox's tertiary note ("(open pipes, pending
/// invocations)"), a radio card's description, a radio row's hint. The CheckBox, RadioCard and RadioRow themes show it
/// when set; it is also the control's accessible help text, so a screen reader reads the description with the label.
/// </summary>
public sealed class ControlHint
{
    /// <summary>The hint text; null or empty shows nothing.</summary>
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<ControlHint, Control, string?>("Text");

    static ControlHint() => TextProperty.Changed.AddClassHandler<Control>(static (control, e) =>
        AutomationProperties.SetHelpText(control, e.GetNewValue<string?>()));

    private ControlHint()
    {
    }

    public static string? GetText(Control control) => control.GetValue(TextProperty);

    public static void SetText(Control control, string? value) => control.SetValue(TextProperty, value);
}
