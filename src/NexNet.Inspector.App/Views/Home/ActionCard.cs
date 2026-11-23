using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Media;

namespace NexNet.Inspector.App.Views.Home;

/// <summary>
/// A home action card (controls/card): a button with a 16 px line icon, a 14 px bold title, a secondary description
/// and a mono shortcut. <see cref="IsDropTarget"/> draws the dashed strong border of "Open a recording";
/// <see cref="IsDragOver"/> is its active state while a file is dragged over the window (outbound border, selected
/// fill, <see cref="DropText"/> in place of the description). Pseudo-classes <c>:droptarget</c> and <c>:dragover</c>.
/// </summary>
[PseudoClasses(":droptarget", ":dragover")]
public sealed class ActionCard : Button
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ActionCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<ActionCard, string?>(nameof(Description));

    public static readonly StyledProperty<string?> ShortcutProperty =
        AvaloniaProperty.Register<ActionCard, string?>(nameof(Shortcut));

    /// <summary>The icon's path in a 16 × 16 box, stroked 1.6 px with <see cref="IconBrush"/>.</summary>
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<ActionCard, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<IBrush?> IconBrushProperty =
        AvaloniaProperty.Register<ActionCard, IBrush?>(nameof(IconBrush));

    public static readonly StyledProperty<bool> IsDropTargetProperty =
        AvaloniaProperty.Register<ActionCard, bool>(nameof(IsDropTarget));

    public static readonly StyledProperty<bool> IsDragOverProperty =
        AvaloniaProperty.Register<ActionCard, bool>(nameof(IsDragOver));

    /// <summary>"Drop to open chat-repro.nntl", shown while <see cref="IsDragOver"/>.</summary>
    public static readonly StyledProperty<string?> DropTextProperty =
        AvaloniaProperty.Register<ActionCard, string?>(nameof(DropText));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IBrush? IconBrush
    {
        get => GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public bool IsDropTarget
    {
        get => GetValue(IsDropTargetProperty);
        set => SetValue(IsDropTargetProperty, value);
    }

    public bool IsDragOver
    {
        get => GetValue(IsDragOverProperty);
        set => SetValue(IsDragOverProperty, value);
    }

    public string? DropText
    {
        get => GetValue(DropTextProperty);
        set => SetValue(DropTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsDropTargetProperty) PseudoClasses.Set(":droptarget", change.GetNewValue<bool>());
        else if (change.Property == IsDragOverProperty) PseudoClasses.Set(":dragover", change.GetNewValue<bool>());
        else if (change.Property == TitleProperty) Avalonia.Automation.AutomationProperties.SetName(this, change.GetNewValue<string?>());
    }
}
