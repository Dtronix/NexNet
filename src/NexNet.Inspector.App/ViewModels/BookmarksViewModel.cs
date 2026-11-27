using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The title bar's ★ bookmarks (I44, D4): the button beside the time readout and its dropdown listing the active
/// document's bookmarks (name, range, scope badge), with inline rename (F2) and delete. Ctrl+Shift+B adds the current
/// view and opens the list with the new name being edited (it defaults to the formatted range); Alt+1…9 jump.
/// </summary>
public sealed partial class BookmarksViewModel : ObservableObject
{
    private readonly DocumentsViewModel _documents;
    private DocumentViewModel? _active;
    private int _editIndex = -1;

    /// <param name="documents">The documents, their bookmarks and the sync bus.</param>
    public BookmarksViewModel(DocumentsViewModel documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
        documents.Bookmarks.Changed += _ => Rebuild();
        documents.PropertyChanged += OnDocumentsChanged;
        documents.Sync.Changed += changes =>
        {
            if ((changes & (SyncChanges.Window | SyncChanges.Scope)) != 0) UpdateCanAdd();
        };
        Follow(documents.Active);
        Rebuild();
    }

    /// <summary>The active document's bookmarks, in list order.</summary>
    public ObservableCollection<BookmarkItem> Items { get; } = [];

    /// <summary>The dropdown is open.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>True when a document with a session is active: the ★ button shows.</summary>
    [ObservableProperty]
    public partial bool HasDocument { get; private set; }

    /// <summary>True when the active document has bookmarks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasItems { get; private set; }

    /// <summary>True when the list is empty (it shows how to add one).</summary>
    public bool IsEmpty => !HasItems;

    /// <summary>The count beside the ★ ("3"); empty with none.</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    /// <summary>The dropdown's heading: "Bookmarks · 3 of 50".</summary>
    [ObservableProperty]
    public partial string HeaderText { get; private set; } = "Bookmarks";

    /// <summary>True when the current view can be bookmarked (a window shows and the recording is under its cap).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool CanAdd { get; private set; }

    /// <summary>The row whose name is being edited; null when none is.</summary>
    [ObservableProperty]
    public partial BookmarkItem? Editing { get; private set; }

    /// <summary>Raised when a row's editor should take the keyboard (the view focuses it and selects its text).</summary>
    public event Action<BookmarkItem>? EditRequested;

    /// <summary>The ★ button: opens or closes the dropdown.</summary>
    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    /// <summary>
    /// Ctrl+Shift+B and the dropdown's "Bookmark current view": adds the current view, opens the dropdown and edits the
    /// new bookmark's name (Enter keeps it, Esc keeps the default).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    public void Add()
    {
        if (_documents.AddBookmark() is null) return;
        IsOpen = true;
        if (Items.Count > 0) BeginRename(Items[^1]);
    }

    /// <summary>A row's click (and Enter on it): shows the bookmark again and closes the dropdown.</summary>
    [RelayCommand]
    private void Jump(BookmarkItem? item)
    {
        if (item is null) return;
        CommitRename();
        IsOpen = false;
        _documents.JumpToBookmark(item.Index);
    }

    /// <summary>A row's ✕ (and Delete on it): deletes the bookmark.</summary>
    [RelayCommand]
    private void Delete(BookmarkItem? item)
    {
        if (item is null || _active is null) return;
        if (Editing is not null) CancelRename();
        _documents.Bookmarks.Remove(_active.BookmarkKey, item.Index);
    }

    /// <summary>F2 on a row: its name becomes editable.</summary>
    public void BeginRename(BookmarkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Editing is { } other && !ReferenceEquals(other, item)) CommitRename();
        item.EditText = item.Name;
        item.IsEditing = true;
        Editing = item;
        _editIndex = item.Index;
        EditRequested?.Invoke(item);
    }

    /// <summary>Enter in the editor (and closing the dropdown): keeps the typed name; a blank one keeps the old name.</summary>
    public void CommitRename()
    {
        if (Editing is not { } item) return;
        EndEdit(item);
        if (_active is not null) _documents.Bookmarks.Rename(_active.BookmarkKey, item.Index, item.EditText);
    }

    /// <summary>Esc in the editor: keeps the old name.</summary>
    public void CancelRename()
    {
        if (Editing is { } item) EndEdit(item);
    }

    private void EndEdit(BookmarkItem item)
    {
        item.IsEditing = false;
        Editing = null;
        _editIndex = -1;
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value) CommitRename();
    }

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DocumentsViewModel.Active) or nameof(DocumentsViewModel.HasDocuments))) return;
        if (ReferenceEquals(_active, _documents.Active)) return;
        CancelRename();
        Follow(_documents.Active);
        Rebuild();
    }

    private void Follow(DocumentViewModel? document)
    {
        if (_active is not null) _active.PropertyChanged -= OnActiveChanged;
        _active = document;
        if (_active is not null) _active.PropertyChanged += OnActiveChanged;
    }

    // A live tab's session (and with it its key) comes and goes with attach and detach.
    private void OnActiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.Session)) Rebuild();
    }

    private void UpdateCanAdd() => CanAdd = _documents.CanBookmark;

    private void Rebuild()
    {
        DocumentViewModel? document = _active;
        IReadOnlyList<ViewBookmark> list = document is null ? [] : _documents.Bookmarks.Get(document.BookmarkKey);
        long origin = document?.Session?.StartTicks ?? 0;
        long frequency = document?.Frequency ?? 1;
        Items.Clear();
        BookmarkItem? editing = null;
        for (int i = 0; i < list.Count; i++)
        {
            BookmarkItem item = new(i, list[i], origin, frequency);
            if (i == _editIndex && Editing is { } was)
            {
                item.EditText = was.EditText;
                item.IsEditing = true;
                editing = item;
            }

            Items.Add(item);
        }

        if (Editing is not null)
        {
            Editing = editing;
            if (editing is null) _editIndex = -1;
        }

        HasDocument = document?.Session is not null;
        HasItems = list.Count > 0;
        CountText = list.Count > 0 ? list.Count.ToString(CultureInfo.InvariantCulture) : "";
        HeaderText = "Bookmarks · " + list.Count.ToString(CultureInfo.InvariantCulture) + " of "
                     + AppSettings.MaxBookmarksPerRecording.ToString(CultureInfo.InvariantCulture);
        UpdateCanAdd();
    }
}

/// <summary>One row of the ★ list.</summary>
public sealed partial class BookmarkItem : ObservableObject
{
    internal BookmarkItem(int index, ViewBookmark bookmark, long origin, long frequency)
    {
        Index = index;
        Bookmark = bookmark;
        Name = bookmark.Name;
        RangeText = TimeWindowReadoutViewModel.Format(bookmark.Window, origin, frequency);
        ShortcutText = index < 9 ? "Alt+" + (index + 1).ToString(CultureInfo.InvariantCulture) : "";
        if (bookmark.Scope is { } scope)
        {
            ScopeTip = "Jumping pushes its scope " + TimeWindowReadoutViewModel.FormatScope(scope.Window, origin, frequency)
                       + (scope.Label.Length > 0 ? " (" + scope.Label + ")" : "");
        }

        EditText = Name;
    }

    /// <summary>Its place in the list (Alt+1 is index 0).</summary>
    public int Index { get; }

    /// <summary>The bookmark.</summary>
    public ViewBookmark Bookmark { get; }

    /// <summary>Its name.</summary>
    public string Name { get; }

    /// <summary>Its window, "1,240.0 – 1,290.0 ms".</summary>
    public string RangeText { get; }

    /// <summary>"Alt+1" … "Alt+9"; empty past the ninth.</summary>
    public string ShortcutText { get; }

    /// <summary>True when the bookmark carries a scope (the row shows the scope badge).</summary>
    public bool HasScope => Bookmark.Scope is not null;

    /// <summary>The scope badge's tooltip; empty without a scope.</summary>
    public string ScopeTip { get; } = "";

    /// <summary>The automation name: "Bookmark 1: name, range".</summary>
    public string AccessibleName => "Bookmark " + (Index + 1).ToString(CultureInfo.InvariantCulture) + ": " + Name + ", " + RangeText
                                    + (HasScope ? ", scoped" : "");

    /// <summary>True while the name is being edited.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>The editor's text.</summary>
    [ObservableProperty]
    public partial string EditText { get; set; }
}
