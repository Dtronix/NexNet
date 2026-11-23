using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// The modal layer of the single window (controls/dialog): a scrim over everything below the title bar with the dialog
/// centred ~60 px under it. Setting <see cref="Dialog"/> opens it, null closes it. While open:
/// <list type="bullet">
/// <item>the keyboard stays inside: Tab cycles through the dialog, and focus that lands outside (a click on the title
/// bar) is sent back in;</item>
/// <item>a press on the scrim is swallowed and never dismisses (UX rules: the scrim does not dismiss);</item>
/// <item>the element marked <see cref="InitialFocusProperty"/> (else the first focusable one) takes the keyboard, and
/// the control that had it gets it back on close.</item>
/// </list>
/// Enter and Esc belong to the <see cref="DialogFrame"/> inside. Scrim and panel appear and leave by an Opacity fade
/// (120 ms, 80 ms when the OS turns animations off): never a brush faded from Transparent, which flashes in dark mode.
/// </summary>
public sealed class DialogHost : ContentControl
{
    /// <summary>The dialog to show (a view, or a view-model the window's data templates turn into one); null closes.</summary>
    public static readonly StyledProperty<object?> DialogProperty =
        AvaloniaProperty.Register<DialogHost, object?>(nameof(Dialog));

    /// <summary>True from the moment <see cref="Dialog"/> is set until it is cleared (the fade-out is not "open").</summary>
    public static readonly DirectProperty<DialogHost, bool> IsOpenProperty =
        AvaloniaProperty.RegisterDirect<DialogHost, bool>(nameof(IsOpen), static h => h.IsOpen);

    /// <summary>Marks the field that takes the keyboard when its dialog opens (connect: the endpoint).</summary>
    public static readonly AttachedProperty<bool> InitialFocusProperty =
        AvaloniaProperty.RegisterAttached<DialogHost, Control, bool>("InitialFocus");

    private bool _isOpen;
    private IInputElement? _returnFocus;
    private TopLevel? _topLevel;
    private DispatcherTimer? _hide;
    private Control? _scrim;

    public DialogHost()
    {
        // Closed, the layer takes no input and no space in hit testing.
        IsVisible = false;
        Opacity = 0;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = MenuMotion.Fade, Easing = MenuMotion.Ease }];
    }

    public object? Dialog
    {
        get => GetValue(DialogProperty);
        set => SetValue(DialogProperty, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetAndRaise(IsOpenProperty, ref _isOpen, value);
    }

    public static bool GetInitialFocus(Control control) => control.GetValue(InitialFocusProperty);

    public static void SetInitialFocus(Control control, bool value) => control.SetValue(InitialFocusProperty, value);

    /// <summary>How long the fade takes; the hidden layer is dropped after it.</summary>
    public static TimeSpan FadeDuration => MenuMotion.Fade;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_scrim is not null) _scrim.PointerPressed -= OnScrimPressed;
        _scrim = e.NameScope.Find<Control>("PART_Scrim");
        if (_scrim is not null) _scrim.PointerPressed += OnScrimPressed;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DialogProperty)
        {
            if (change.NewValue is { } dialog) Open(dialog);
            else Close();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(GotFocusEvent, OnAnyGotFocus);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Open(object dialog)
    {
        _hide?.Stop();
        if (!IsOpen) _returnFocus = _topLevel?.FocusManager?.GetFocusedElement();
        Content = dialog;
        IsVisible = true;
        IsOpen = true;
        Opacity = 1;
        // The dialog's template and bindings are only in place after layout.
        Dispatcher.UIThread.Post(FocusInitial, DispatcherPriority.Loaded);
    }

    private void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Opacity = 0;
        IInputElement? back = _returnFocus;
        _returnFocus = null;
        if (back is Visual visual && TopLevel.GetTopLevel(visual) is not null && back.Focusable && back.IsEffectivelyEnabled) back.Focus();
        else _topLevel?.Focus();

        // The content stays until the fade has run, so the panel fades with the scrim instead of vanishing first.
        _hide ??= new DispatcherTimer { Interval = FadeDuration };
        _hide.Tick -= OnHide;
        _hide.Tick += OnHide;
        _hide.Start();
    }

    private void OnHide(object? sender, EventArgs e)
    {
        _hide?.Stop();
        if (IsOpen) return;
        Content = null;
        IsVisible = false;
    }

    /// <summary>The marked field, else the first focusable control of the dialog, takes the keyboard.</summary>
    public void FocusInitial()
    {
        if (!IsOpen) return;
        try
        {
            InputElement? first = null;
            foreach (Visual visual in this.GetVisualDescendants())
            {
                if (visual is not InputElement { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true } element) continue;
                if (element is Control control && GetInitialFocus(control))
                {
                    element.Focus(NavigationMethod.Tab);
                    return;
                }

                first ??= element;
            }

            first?.Focus(NavigationMethod.Tab);
        }
        catch (InvalidOperationException)
        {
            // The window closed between the post and the call: there is nothing left to focus.
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!IsOpen || e.Source is not Visual target || Contains(target)) return;
        // Focus escaped (a click on the title bar, a stray Tab from outside): send it back in.
        Dispatcher.UIThread.Post(FocusInitial, DispatcherPriority.Input);
    }

    /// <summary>
    /// Inside the dialog: a visual descendant, or a logical one (a select's list lives in a popup, outside the visual
    /// tree of the host but logically under its ComboBox).
    /// </summary>
    private bool Contains(Visual target) =>
        this.IsVisualAncestorOf(target) || (target is ILogical logical && ((ILogical)this).IsLogicalAncestorOf(logical));
}
