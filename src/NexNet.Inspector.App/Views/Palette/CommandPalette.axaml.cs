using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Model.Palette;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Palette;

/// <summary>
/// The command palette's view over <see cref="CommandPaletteViewModel"/>. The query box keeps the keyboard while the
/// palette is open: the keys that move, run, switch scope or close are taken before the box sees them, a click on a
/// pill hands the keyboard straight back, and on close the keyboard returns to where it was.
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private const int PageItems = 8;

    private CommandPaletteViewModel? _model;
    private IInputElement? _returnFocus;

    public CommandPalette()
    {
        InitializeComponent();
        // Tunnelling: the query box would otherwise take Up/Down/Home/End and Tab for itself.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Scrim.PointerPressed += OnScrimPressed;
        Results.AddHandler(PointerPressedEvent, OnResultPressed, RoutingStrategies.Bubble);
        Scopes.SelectionChanged += (_, _) => FocusInput();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.Closed -= OnClosed;
        }

        _model = DataContext as CommandPaletteViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
            _model.Closed += OnClosed;
            if (_model.IsOpen) OnOpened();
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is null) return;
        switch (e.PropertyName)
        {
            case nameof(CommandPaletteViewModel.IsOpen) when _model.IsOpen:
                OnOpened();
                break;
            case nameof(CommandPaletteViewModel.SelectedIndex) when _model.SelectedIndex >= 0:
                Results.ScrollIntoView(_model.SelectedIndex);
                break;
        }
    }

    private void OnOpened()
    {
        IInputElement? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is not Visual visual || !this.IsVisualAncestorOf(visual)) _returnFocus = focused;
        // The palette is laid out (made visible) on this turn: focus once it is.
        Dispatcher.UIThread.Post(FocusInput, DispatcherPriority.Loaded);
    }

    private void OnClosed()
    {
        IInputElement? back = _returnFocus;
        _returnFocus = null;
        if (back is Visual visual && TopLevel.GetTopLevel(visual) is not null && back.Focusable && back.IsEffectivelyEnabled) back.Focus();
        else TopLevel.GetTopLevel(this)?.Focus();
    }

    // The caret goes after the query, so typing continues a prefix the palette was opened with (":" for Go to time).
    private void FocusInput()
    {
        if (_model is not { IsOpen: true }) return;
        try
        {
            Input.Focus(NavigationMethod.Tab);
            Input.ClearSelection();
            Input.CaretIndex = Input.Text?.Length ?? 0;
        }
        catch (InvalidOperationException)
        {
            // The window closed between the post and the call: there is nothing left to focus.
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is not { IsOpen: true } model) return;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        switch (e.Key)
        {
            case Key.Down:
                model.Move(1);
                break;
            case Key.Up:
                model.Move(-1);
                break;
            case Key.PageDown:
                model.Move(PageItems);
                break;
            case Key.PageUp:
                model.Move(-PageItems);
                break;
            case Key.Enter:
                model.Run();
                break;
            case Key.Escape:
                model.Close();
                break;
            case Key.Tab:
                model.CycleScope(backwards: shift);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        // Unlike a dialog's, the palette's scrim dismisses: nothing typed in it is lost that matters.
        e.Handled = true;
        _model?.Close();
    }

    private void OnResultPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_model is null || !e.GetCurrentPoint(Results).Properties.IsLeftButtonPressed) return;
        if (e.Source is not StyledElement { DataContext: PaletteRow { IsItem: true } row }) return;
        e.Handled = true;
        _model.Select(row);
        _model.Run(row);
    }
}
