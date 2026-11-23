using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// A dialog's panel (controls/dialog): the 36 px title row (title, optional mono subtitle such as a file name, the
/// 28 px close ✕), the body (<see cref="ContentControl.Content"/>, 14 px padding) and the footer (mono hint on the
/// left, or <see cref="FooterLead"/>; <see cref="Footer"/> buttons on the right). Enter runs <see cref="PrimaryCommand"/> and Esc
/// <see cref="CancelCommand"/> from anywhere inside, unless a control there used the key itself (a focused button
/// presses on Enter, an open select closes on Esc). The width is the caller's (connect 540, open-recording 720).
/// </summary>
public sealed class DialogFrame : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogFrame, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<DialogFrame, string?>(nameof(Subtitle));

    /// <summary>The footer hint ("Enter to connect · Esc to cancel").</summary>
    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<DialogFrame, string?>(nameof(Hint));

    /// <summary>The footer's right side: secondary then primary button, 8 px apart.</summary>
    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<DialogFrame, object?>(nameof(Footer));

    /// <summary>The footer's left side, in place of the hint (open-recording: "Compare two sessions…").</summary>
    public static readonly StyledProperty<object?> FooterLeadProperty =
        AvaloniaProperty.Register<DialogFrame, object?>(nameof(FooterLead));

    public static readonly StyledProperty<ICommand?> PrimaryCommandProperty =
        AvaloniaProperty.Register<DialogFrame, ICommand?>(nameof(PrimaryCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<DialogFrame, ICommand?>(nameof(CancelCommand));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public object? FooterLead
    {
        get => GetValue(FooterLeadProperty);
        set => SetValue(FooterLeadProperty, value);
    }

    public ICommand? PrimaryCommand
    {
        get => GetValue(PrimaryCommandProperty);
        set => SetValue(PrimaryCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) Avalonia.Automation.AutomationProperties.SetName(this, change.GetNewValue<string?>());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        ICommand? command = e.Key switch
        {
            Key.Enter => PrimaryCommand,
            Key.Escape => CancelCommand,
            _ => null,
        };
        if (command is null) return;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }
}
