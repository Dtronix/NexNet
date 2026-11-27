using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace NexNet.Inspector.App.Views.Home;

/// <summary>
/// A home list card (controls/card): a 24 px chrome header with <see cref="Title"/> over 26 px rows (the item
/// template's), or <see cref="EmptyText"/> in tertiary when there are none (pseudo-class <c>:empty</c>). A click on a
/// row, or Enter on the focused one, runs <see cref="ItemCommand"/> with the row's item: the rows are buttons.
/// </summary>
[PseudoClasses(":empty")]
public sealed class ListCard : ListBox
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ListCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<ListCard, string?>(nameof(EmptyText));

    public static readonly StyledProperty<ICommand?> ItemCommandProperty =
        AvaloniaProperty.Register<ListCard, ICommand?>(nameof(ItemCommand));

    public ListCard()
    {
        PseudoClasses.Set(":empty", true);
        AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        // Tunnel: the row under the keyboard takes Enter for its own selection before it would bubble here.
        AddHandler(KeyDownEvent, OnRowKey, RoutingStrategies.Tunnel);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public ICommand? ItemCommand
    {
        get => GetValue(ItemCommandProperty);
        set => SetValue(ItemCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemCountProperty) PseudoClasses.Set(":empty", change.GetNewValue<int>() == 0);
        else if (change.Property == TitleProperty) Avalonia.Automation.AutomationProperties.SetName(this, change.GetNewValue<string?>());
    }

    private void OnRowKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || e.Source is not Visual source) return;
        ListBoxItem? row = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        object? item = row is not null ? ItemFromContainer(row) : SelectedItem;
        if (item is not null) e.Handled = Activate(item);
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        ListBoxItem? row = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (row is null || ItemFromContainer(row) is not { } item) return;
        e.Handled = Activate(item);
    }

    /// <summary>Runs <see cref="ItemCommand"/> for a row; false when there is none or it cannot run.</summary>
    public bool Activate(object item)
    {
        if (ItemCommand is not { } command || !command.CanExecute(item)) return false;
        command.Execute(item);
        return true;
    }
}
