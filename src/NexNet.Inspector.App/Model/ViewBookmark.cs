using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Model;

/// <summary>
/// A bookmark (D3, D4): a named view of one recording, the time window it showed and the scope it was in, if any. Times
/// are the capture's own Stopwatch ticks, so a bookmark stays valid in a saved or exported copy of the same capture.
/// </summary>
/// <param name="Name">What the ★ list shows; by default the formatted range.</param>
/// <param name="Window">The time window to show again.</param>
/// <param name="Scope">The scope the document was in when the bookmark was added; a jump pushes it again.</param>
/// <param name="CreatedUtc">When it was added.</param>
public readonly record struct ViewBookmark(string Name, TimeWindow Window, TimeScope? Scope, DateTimeOffset CreatedUtc)
{
    /// <summary>The longest name kept; a longer one is cut.</summary>
    public const int MaxNameLength = 120;

    /// <summary>True when <paramref name="other"/> shows the same view (window and scope), whatever its name.</summary>
    public bool SameView(in ViewBookmark other) =>
        Window == other.Window && Scope?.Start == other.Scope?.Start && Scope?.End == other.Scope?.End;

    /// <summary>The form kept in <c>settings.json</c>.</summary>
    public SavedBookmark ToSaved() =>
        new(Name, Window.Start, Window.End, Scope is { } s ? new SavedScope(s.Start, s.End, s.Label) : null, CreatedUtc);

    /// <summary>A bookmark read back from <c>settings.json</c>.</summary>
    public static ViewBookmark From(in SavedBookmark saved) =>
        new(saved.Name, new TimeWindow(saved.Start, saved.End),
            saved.Scope is { } s ? new TimeScope(s.Start, s.End, s.Label ?? "") : null, saved.Created);

    /// <summary>A name made fit to keep: trimmed and cut to <see cref="MaxNameLength"/>; null when blank.</summary>
    public static string? CleanName(string? name)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }
}

/// <summary>
/// Which recording a set of bookmarks belongs to (D3). A session of a file has a persistent key, under which
/// <see cref="AppSettings.Bookmarks"/> keeps its bookmarks across runs; a live document has an in-memory key, and its
/// bookmarks move to the saved file's key when it is saved (re-keying).
/// </summary>
/// <remarks>
/// The persistent key is the lowercase hex SHA-256 of, in order: the body of the file's Header block exactly as stored
/// (the MessagePack array of nntl-format §4.1: format version, wall clock and stopwatch anchor, producer, host), then
/// the session id (8 bytes, little-endian), the session's transport-op block count (4 bytes) and its last indexed tick
/// (8 bytes), all read from the file's index. The header alone is the recording's identity: its anchor is taken when the
/// capture starts, so two recordings never share one; the session id tells the sessions of one file apart. Saving or
/// exporting copies the header, so the op count and last tick tell a section export (or a later save of a growing live
/// session) apart from the full recording, while a whole copy of a file keys like the file. Cheap: one small block read
/// and the in-memory index; nothing depends on the file's path, name or modification time, so a renamed or moved file
/// keeps its bookmarks.
/// </remarks>
/// <param name="Value">The key text: 64 hex digits, or "live:&lt;document&gt;:&lt;session&gt;" for an in-memory key.</param>
/// <param name="IsPersistent">True for a file's key, kept in the settings.</param>
public readonly record struct RecordingKey(string Value, bool IsPersistent)
{
    /// <summary>True for the default value (no recording).</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>A live document's in-memory key: its tab and the attached session.</summary>
    public static RecordingKey Live(int documentId, long sessionId) =>
        new("live:" + documentId.ToString(CultureInfo.InvariantCulture) + ":" + sessionId.ToString("X", CultureInfo.InvariantCulture), false);

    /// <summary>The persistent key of session <paramref name="sessionId"/> of <paramref name="recording"/>.</summary>
    /// <exception cref="ArgumentException">The recording holds no such session.</exception>
    /// <exception cref="NntlFormatException">The header block cannot be read.</exception>
    public static RecordingKey Of(RecordingFile recording, long sessionId)
    {
        ArgumentNullException.ThrowIfNull(recording);
        IReadOnlyList<NntlSessionOpen> sessions = recording.Sessions;
        int found = -1;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (sessions[i].SessionId == sessionId)
            {
                found = i;
                break;
            }
        }

        if (found < 0) throw new ArgumentException("The recording holds no such session.", nameof(sessionId));
        if (!recording.Reader.TryReadBlock(0, out NntlBlock header) || header.Kind != NntlBlockKind.Header)
            throw new NntlFormatException("The recording's header block cannot be read.");

        RecordedSessionSummary summary = recording.Summarize(sessions[found]);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header.Body.Span);
        Span<byte> tail = stackalloc byte[20];
        BinaryPrimitives.WriteInt64LittleEndian(tail, sessionId);
        BinaryPrimitives.WriteInt32LittleEndian(tail[8..], summary.Ops);
        BinaryPrimitives.WriteInt64LittleEndian(tail[12..], summary.LastTicks);
        hash.AppendData(tail);
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return new RecordingKey(Convert.ToHexStringLower(digest), true);
    }

    /// <summary>The persistent key of a session of the recording at <paramref name="path"/> (opens and closes it).</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="NntlFormatException">Not a recording.</exception>
    public static RecordingKey Of(string path, long sessionId)
    {
        using RecordingFile recording = RecordingFile.Open(path);
        return Of(recording, sessionId);
    }
}
