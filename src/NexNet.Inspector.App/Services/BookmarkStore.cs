using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Services;

/// <summary>
/// The bookmarks of every recording (I44, D3), by <see cref="RecordingKey"/>. A file session's bookmarks live in
/// <see cref="AppSettings.Bookmarks"/> and are saved on every change; a live document's stay in memory until its
/// session is saved, when <see cref="CopyTo"/> moves them to the saved file's key. Without a settings store (tests,
/// the self-check) every key stays in memory. UI thread only.
/// </summary>
public sealed class BookmarkStore
{
    private static readonly ViewBookmark[] None = [];

    private readonly SettingsStore? _store;
    private readonly TimeProvider _time;

    // Each key's list as the ★ list shows it; persistent keys are loaded from the settings on first use.
    private readonly Dictionary<string, List<ViewBookmark>> _lists = new(StringComparer.Ordinal);

    // The persistent keys among them (the total cap can drop those from the settings).
    private readonly HashSet<string> _persistent = new(StringComparer.Ordinal);

    /// <param name="store">Where persistent keys are saved; null keeps everything for this run.</param>
    /// <param name="time">The clock of "created" and "last used".</param>
    public BookmarkStore(SettingsStore? store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a key's bookmarks changed.</summary>
    public event Action<RecordingKey>? Changed;

    /// <summary>The clock bookmarks are stamped with.</summary>
    public TimeProvider Time => _time;

    /// <summary>The bookmarks of <paramref name="key"/>, in list order; empty for none. A live view: copy it to keep it.</summary>
    public IReadOnlyList<ViewBookmark> Get(RecordingKey key) => key.IsEmpty ? None : (IReadOnlyList<ViewBookmark>?)Find(key) ?? None;

    /// <summary>True when <paramref name="key"/> can take another bookmark (it is under <see cref="AppSettings.MaxBookmarksPerRecording"/>).</summary>
    public bool CanAdd(RecordingKey key) => !key.IsEmpty && Get(key).Count < AppSettings.MaxBookmarksPerRecording;

    /// <summary>Adds a bookmark at the end of <paramref name="key"/>'s list.</summary>
    /// <returns>False when the recording already keeps the most it can, or the name is blank.</returns>
    public bool Add(RecordingKey key, ViewBookmark bookmark)
    {
        if (!CanAdd(key) || ViewBookmark.CleanName(bookmark.Name) is not { } name || bookmark.Window.IsEmpty) return false;
        Ensure(key).Add(bookmark with { Name = name });
        Commit(key);
        return true;
    }

    /// <summary>Renames bookmark <paramref name="index"/> of <paramref name="key"/>.</summary>
    /// <returns>False for a blank name or a bookmark that does not exist.</returns>
    public bool Rename(RecordingKey key, int index, string? name)
    {
        if (Find(key) is not { } list || (uint)index >= (uint)list.Count || ViewBookmark.CleanName(name) is not { } clean) return false;
        if (string.Equals(list[index].Name, clean, StringComparison.Ordinal)) return true;
        list[index] = list[index] with { Name = clean };
        Commit(key);
        return true;
    }

    /// <summary>Deletes bookmark <paramref name="index"/> of <paramref name="key"/>.</summary>
    /// <returns>False when it does not exist.</returns>
    public bool Remove(RecordingKey key, int index)
    {
        if (Find(key) is not { } list || (uint)index >= (uint)list.Count) return false;
        list.RemoveAt(index);
        Commit(key);
        return true;
    }

    /// <summary>Forgets an in-memory key's bookmarks (a live tab closed or detached); a persistent key is left alone.</summary>
    public void Forget(RecordingKey key)
    {
        if (key.IsEmpty || key.IsPersistent) return;
        if (_lists.Remove(key.Value)) Changed?.Invoke(key);
    }

    /// <summary>
    /// Re-keying (save, Export section): copies the bookmarks of <paramref name="from"/> to <paramref name="to"/>, after
    /// the ones it holds, skipping views it already has and stopping at <see cref="AppSettings.MaxBookmarksPerRecording"/>.
    /// With <paramref name="within"/>, only bookmarks whose window lies inside it are copied, their scope cut to it (a scope
    /// left with no time is dropped).
    /// </summary>
    /// <returns>How many were copied.</returns>
    public int CopyTo(RecordingKey from, RecordingKey to, TimeWindow within = default)
    {
        if (from.IsEmpty || to.IsEmpty || from == to || Find(from) is not { Count: > 0 } source) return 0;
        List<ViewBookmark> target = Ensure(to);
        int copied = 0;
        foreach (ViewBookmark bookmark in source)
        {
            if (target.Count >= AppSettings.MaxBookmarksPerRecording) break;
            ViewBookmark copy = bookmark;
            if (!within.IsEmpty)
            {
                if (bookmark.Window.Start < within.Start || bookmark.Window.End > within.End) continue;
                if (bookmark.Scope is { } scope)
                {
                    TimeScope cut = scope with { Start = Math.Max(scope.Start, within.Start), End = Math.Min(scope.End, within.End) };
                    copy = copy with { Scope = cut.IsEmpty ? null : cut };
                }
            }

            bool seen = false;
            foreach (ViewBookmark kept in target) seen |= kept.SameView(copy);
            if (seen) continue;
            target.Add(copy);
            copied++;
        }

        if (copied > 0) Commit(to);
        else if (target.Count == 0)
        {
            _lists.Remove(to.Value);
            _persistent.Remove(to.Value);
        }
        return copied;
    }

    private List<ViewBookmark>? Find(RecordingKey key)
    {
        if (key.IsEmpty) return null;
        if (_lists.TryGetValue(key.Value, out List<ViewBookmark>? list)) return list;
        if (!key.IsPersistent || _store is null) return null;

        IReadOnlyList<SavedBookmark> saved = _store.Current.BookmarksFor(key.Value);
        if (saved.Count == 0) return null;
        list = new List<ViewBookmark>(saved.Count);
        foreach (SavedBookmark item in saved) list.Add(ViewBookmark.From(item));
        _lists[key.Value] = list;
        _persistent.Add(key.Value);
        return list;
    }

    private List<ViewBookmark> Ensure(RecordingKey key)
    {
        if (Find(key) is { } list) return list;
        list = [];
        _lists[key.Value] = list;
        if (key.IsPersistent) _persistent.Add(key.Value);
        return list;
    }

    private void Commit(RecordingKey key)
    {
        if (key.IsPersistent && _store is not null)
        {
            List<ViewBookmark> list = _lists[key.Value];
            SavedBookmark[] items = new SavedBookmark[list.Count];
            for (int i = 0; i < items.Length; i++) items[i] = list[i].ToSaved();
            _store.Update(s => s.WithBookmarks(key.Value, items, _time.GetUtcNow()));

            // The total cap may have dropped older recordings: their cached lists go with them.
            List<string>? dropped = null;
            foreach (string cached in _persistent)
            {
                if (_store.Current.BookmarksFor(cached).Count == 0) (dropped ??= []).Add(cached);
            }

            if (dropped is not null)
            {
                foreach (string gone in dropped)
                {
                    _lists.Remove(gone);
                    _persistent.Remove(gone);
                }
            }
        }

        Changed?.Invoke(key);
    }
}
