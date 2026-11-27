using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Shell;

/// <summary>
/// The bookmarks dropdown's view (BookmarksPopover.axaml) over <see cref="BookmarksViewModel"/>: the keys of the list
/// (F2 rename, Delete, Esc; Enter and Esc in the name editor), handled here so the window's own F2 (Legend) and Esc
/// never see them, and the editor's focus when a rename starts.
/// </summary>
public sealed partial class BookmarksPopover : UserControl
{
    private BookmarksViewModel? _model;

    public BookmarksPopover()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        // Key bindings are matched from the focused element up before the key is routed: this one wins over the
        // window's F2 (Legend) while the keyboard is in the list.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F2), Command = new RelayCommand(RenameFocused) });
    }

    // F2: the focused row's name becomes editable (inside the editor it does nothing).
    private void RenameFocused()
    {
        if (_model is not { Editing: null } model) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control { DataContext: BookmarkItem item }) model.BeginRename(item);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.EditRequested -= OnEditRequested;
        _model = DataContext as BookmarksViewModel;
        if (_model is not null) _model.EditRequested += OnEditRequested;
    }

    /// <summary>Where the keyboard goes when the dropdown opens: the name being edited, else the first row, else Add.</summary>
    public void FocusInitial()
    {
        if (_model?.Editing is { } editing && FocusEditor(editing)) return;
        if (!FocusRow(0)) AddAction.Focus(NavigationMethod.Tab);
    }

    /// <summary>Gives the keyboard to row <paramref name="index"/>'s button.</summary>
    /// <returns>False when the row is not realized.</returns>
    public bool FocusRow(int index)
    {
        if (_model is null || (uint)index >= (uint)_model.Items.Count) return false;
        if (Rows.ContainerFromIndex(index) is not { } container) return false;
        foreach (Button button in container.GetVisualDescendants().OfType<Button>())
        {
            if (button.Name != "BookmarkRow") continue;
            button.Focus(NavigationMethod.Tab);
            return true;
        }

        return false;
    }

    private bool FocusEditor(BookmarkItem item)
    {
        if (_model is null) return false;
        int index = _model.Items.IndexOf(item);
        if (index < 0 || Rows.ContainerFromIndex(index) is not { } container) return false;
        foreach (TextBox box in container.GetVisualDescendants().OfType<TextBox>())
        {
            if (box.Name != "NameEditor") continue;
            box.Focus(NavigationMethod.Tab);
            box.SelectAll();
            return true;
        }

        return false;
    }

    // The row's editor exists once the row is laid out with it shown.
    private void OnEditRequested(BookmarkItem item) =>
        Dispatcher.UIThread.Post(() => FocusEditor(item), DispatcherPriority.Loaded);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _model is not { } model) return;
        if (model.Editing is { } editing)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    model.CommitRename();
                    RefocusRow(editing.Index);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    model.CancelRename();
                    RefocusRow(editing.Index);
                    e.Handled = true;
                    break;
            }

            return;
        }

        BookmarkItem? focused = (e.Source as Control)?.DataContext as BookmarkItem;
        switch (e.Key)
        {
            case Key.Delete when focused is not null:
                int index = focused.Index;
                model.DeleteCommand.Execute(focused);
                RefocusRow(Math.Min(index, model.Items.Count - 1));
                e.Handled = true;
                break;
            case Key.Escape:
                model.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    // The rows are rebuilt after a change: the keyboard returns to the same place once they are laid out.
    private void RefocusRow(int index) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!FocusRow(index)) AddAction.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
}
