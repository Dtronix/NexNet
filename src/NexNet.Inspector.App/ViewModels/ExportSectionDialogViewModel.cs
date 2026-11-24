using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>Which part of a session Export section as .nntl… writes.</summary>
public enum ExportRangeKind : byte
{
    /// <summary>The time window the views show.</summary>
    Window = 0,

    /// <summary>The selected message's frames, first to last.</summary>
    Message = 1,

    /// <summary>The traced message's frames.</summary>
    Trace = 2,

    /// <summary>A start and end typed in milliseconds from the capture's start.</summary>
    Typed = 3,
}

/// <summary>
/// Export section as .nntl… (Ctrl+Shift+S): writes part of the active document's session to a recording of its own, to
/// hand to someone else for debugging. The range is the current time window (default), the selected message's span, the
/// traced message's frames, or a typed start–end in ms; options: include the handshake (the file then frames and decodes
/// on its own), embed the schema, and keep redaction (always: the export copies captured bytes, which are redacted unless
/// the source included secrets). Export asks where to save (suggesting "chat-0x1BCB-364.7-365.6s.nntl") and writes on
/// the thread pool (<see cref="CaptureExport.WriteRange"/>). Works for live and file documents alike.
/// </summary>
public sealed partial class ExportSectionDialogViewModel : ObservableObject
{
    private readonly DocumentsViewModel _documents;
    private readonly DocumentViewModel _document;
    private readonly CaptureSession _session;
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly (long Start, long End)? _message;
    private readonly (long Start, long End)? _trace;
    private readonly long _origin;
    private readonly long _frequency;
    private readonly TimeWindow _window;

    /// <param name="documents">The documents: the sync bus, the save picker and the recents.</param>
    /// <param name="document">The document to export from; it must hold a session.</param>
    /// <param name="initial">The range chosen first; one that is not available falls back to the time window.</param>
    public ExportSectionDialogViewModel(DocumentsViewModel documents, DocumentViewModel document, ExportRangeKind initial = ExportRangeKind.Window)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(document);
        _documents = documents;
        _document = document;
        _session = document.Session ?? throw new ArgumentException("The document holds no session.", nameof(document));
        _frequency = Math.Max(1, document.Frequency);
        lock (_session.Gate)
        {
            _origin = _session.StartTicks;
            var sync = documents.Sync;
            var window = sync.Window;
            _window = window.IsEmpty ? new TimeWindow(_session.StartTicks, Math.Max(_session.EndTicks, _session.StartTicks + 1)) : window;
            if (!sync.Selection.IsNone && sync.Selection.Kind is SelectionKind.Frame or SelectionKind.Message or SelectionKind.Entity
                && MessageTrace.Resolve(_session, sync.Selection) is { IsActive: true } selected)
                _message = (selected.Start, selected.End);
            if (sync.Trace is { IsActive: true } trace)
                _trace = (trace.Start, trace.End);
        }

        FromText = Milliseconds(_window.Start);
        ToText = Milliseconds(_window.End);
        Range = initial switch
        {
            ExportRangeKind.Message when _message is null => ExportRangeKind.Window,
            ExportRangeKind.Trace when _trace is null => ExportRangeKind.Window,
            _ => initial,
        };
    }

    /// <summary>Raised once the dialog is done (exported, or canceled): the host removes it.</summary>
    public event Action<ExportSectionDialogViewModel>? Closed;

    public string Title => "Export section as .nntl";

    public string Hint => "Enter to export · Esc to cancel";

    /// <summary>The document exported from.</summary>
    public DocumentViewModel Document => _document;

    /// <summary>Which range is exported.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindow), nameof(IsMessage), nameof(IsTrace), nameof(IsTyped), nameof(Summary), nameof(SuggestedName))]
    public partial ExportRangeKind Range { get; set; }

    /// <summary>The time window radio.</summary>
    public bool IsWindow
    {
        get => Range == ExportRangeKind.Window;
        set
        {
            if (value) Range = ExportRangeKind.Window;
        }
    }

    /// <summary>The selected message radio.</summary>
    public bool IsMessage
    {
        get => Range == ExportRangeKind.Message;
        set
        {
            if (value && HasMessage) Range = ExportRangeKind.Message;
        }
    }

    /// <summary>The traced message radio.</summary>
    public bool IsTrace
    {
        get => Range == ExportRangeKind.Trace;
        set
        {
            if (value && HasTrace) Range = ExportRangeKind.Trace;
        }
    }

    /// <summary>The typed range radio.</summary>
    public bool IsTyped
    {
        get => Range == ExportRangeKind.Typed;
        set
        {
            if (value) Range = ExportRangeKind.Typed;
        }
    }

    /// <summary>True when a message is selected (its radio is enabled).</summary>
    public bool HasMessage => _message is not null;

    /// <summary>True when a message is traced.</summary>
    public bool HasTrace => _trace is not null;

    /// <summary>"364.7 – 365.6 s": the window radio's hint.</summary>
    public string WindowText => Span(_window.Start, _window.End);

    /// <summary>The selected message radio's hint; "nothing selected" without one.</summary>
    public string MessageText => _message is { } m ? Span(m.Start, m.End) : "nothing selected";

    /// <summary>The traced message radio's hint; "nothing traced" without one.</summary>
    public string TraceText => _trace is { } t ? Span(t.Start, t.End) : "nothing traced";

    /// <summary>The typed range's start, in ms from the capture's start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(SuggestedName))]
    public partial string FromText { get; set; }

    /// <summary>The typed range's end, in ms from the capture's start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(SuggestedName))]
    public partial string ToText { get; set; }

    /// <summary>Write the preamble and greetings too, so the file decodes on its own.</summary>
    [ObservableProperty]
    public partial bool IncludeHandshake { get; set; } = true;

    /// <summary>Embed the schema the session names.</summary>
    [ObservableProperty]
    public partial bool IncludeSchema { get; set; } = true;

    /// <summary>Redaction is always kept (a disabled, checked box says so).</summary>
    public bool KeepRedaction => true;

    /// <summary>What is exported: "Exports 364.7 – 365.6 s (0.9 s) of Live · 0x1BCB".</summary>
    public string Summary => TryGetRange(out var from, out var to, out _)
        ? "Exports " + Span(from, to) + " of " + _document.Title
        : "Type a start and an end in ms from the capture's start";

    /// <summary>Why the export cannot run or failed; null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    /// <summary>True while the error line shows.</summary>
    public bool HasError => Error is not null;

    /// <summary>True while the file is written.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsExporting { get; private set; }

    /// <summary>Where the section was written; null when canceled.</summary>
    public Task<string?> Result => _result.Task;

    /// <summary>"chat-0x1BCB-364.7-365.6s.nntl": the nexus, the session and the range in seconds.</summary>
    public string SuggestedName
    {
        get
        {
            TryGetRange(out var from, out var to, out _);
            return SuggestFileName(_session, from - _origin, to - _origin, _frequency);
        }
    }

    /// <summary>A section's suggested file name: "chat-0x1BCB-364.7-365.6s.nntl".</summary>
    /// <param name="session">The session (its schema names the nexus).</param>
    /// <param name="from">The section's start, in ticks from the capture's start.</param>
    /// <param name="to">The section's end, in ticks from the capture's start.</param>
    /// <param name="frequency">Ticks per second.</param>
    public static string SuggestFileName(CaptureSession session, long from, long to, long frequency)
    {
        ArgumentNullException.ThrowIfNull(session);
        var stem = session.Role == NntlSessionRole.Client ? "client" : "server";
        if (session.Schema.Schema is { Exports.Count: > 0 } schema)
        {
            var name = schema.Exports[0].Name;
            foreach (var suffix in (ReadOnlySpan<string>)["ServerNexus", "ClientNexus", "Nexus"])
            {
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    name = name[..^suffix.Length];
                    break;
                }
            }

            stem = name.ToLowerInvariant();
        }

        var f = Math.Max(1, frequency);
        return stem + "-" + LiveDocumentViewModel.SessionHex(session.SessionId) + "-"
               + (from / (double)f).ToString("0.0", CultureInfo.InvariantCulture) + "-"
               + (to / (double)f).ToString("0.0", CultureInfo.InvariantCulture) + "s.nntl";
    }

    /// <summary>The chosen range in the session's ticks; false when the typed one does not parse.</summary>
    public bool TryGetRange(out long from, out long to, out string? error)
    {
        error = null;
        switch (Range)
        {
            case ExportRangeKind.Message when _message is { } m:
                (from, to) = m;
                return true;
            case ExportRangeKind.Trace when _trace is { } t:
                (from, to) = t;
                return true;
            case ExportRangeKind.Typed:
                if (!TryParseMs(FromText, out var a) || !TryParseMs(ToText, out var b))
                {
                    from = to = 0;
                    error = "Type the start and end in ms, as the time column shows them (364700.5)";
                    return false;
                }

                if (b < a)
                    (a, b) = (b, a);
                from = _origin + TimeWindow.TicksFromMilliseconds(a, _frequency);
                to = _origin + TimeWindow.TicksFromMilliseconds(b, _frequency);
                return true;
            default:
                (from, to) = (_window.Start, _window.End);
                return true;
        }
    }

    /// <summary>Parses ms as the time column writes them ("1,234.567" or "1234.567").</summary>
    public static bool TryParseMs(string? text, out double ms) =>
        double.TryParse((text ?? "").Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out ms)
        && double.IsFinite(ms) && ms >= 0;

    partial void OnFromTextChanged(string value) => Error = null;

    partial void OnToTextChanged(string value) => Error = null;

    partial void OnRangeChanged(ExportRangeKind value) => Error = null;

    /// <summary>Enter and Export…: asks where, then writes the section; the dialog closes once written.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        if (_result.Task.IsCompleted) return;
        if (!TryGetRange(out var from, out var to, out var error))
        {
            Error = error;
            return;
        }

        if (_documents.SavePicker is not { } pick)
        {
            Error = "There is no place to save to here";
            return;
        }

        if (await pick(SuggestedName).ConfigureAwait(true) is not { Length: > 0 } path)
            return;

        IsExporting = true;
        try
        {
            await ExportToAsync(path, new CaptureExportRange(from, to, IncludeHandshake, IncludeSchema)).ConfigureAwait(true);
            Finish(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NntlFormatException or ArgumentException)
        {
            Error = "Could not export " + Path.GetFileName(path) + ": " + e.Message;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private bool CanExport() => !IsExporting;

    /// <summary>
    /// Writes the section to <paramref name="path"/> on the thread pool, whole or not at all (written beside it and
    /// moved over it), and puts it first in the recents. A file still indexing is exported once its index completes.
    /// </summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public async Task<CaptureRangeResult> ExportToAsync(string path, CaptureExportRange range)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_document is FileDocumentViewModel file)
            await file.Indexing.ConfigureAwait(true);
        var full = Path.GetFullPath(path);
        var session = _session;
        var result = await Task.Run(() =>
        {
            var partial = full + ".partial";
            try
            {
                CaptureRangeResult written;
                using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                    written = CaptureExport.WriteRange(session, output, range);
                File.Move(partial, full, overwrite: true);
                return written;
            }
            catch
            {
                File.Delete(partial);
                throw;
            }
        }).ConfigureAwait(true);
        _documents.AddRecentRecording(full, 1);
        return result;
    }

    /// <summary>Esc and Cancel.</summary>
    [RelayCommand]
    private void Cancel() => Finish(null);

    private void Finish(string? path)
    {
        if (!_result.TrySetResult(path)) return;
        Closed?.Invoke(this);
    }

    private string Milliseconds(long ticks) =>
        TimeWindow.MillisecondsFromTicks(ticks - _origin, _frequency).ToString("0.000", CultureInfo.InvariantCulture);

    private string Span(long from, long to)
    {
        double a = (from - _origin) / (double)_frequency;
        double b = (to - _origin) / (double)_frequency;
        return a.ToString("0.000", CultureInfo.InvariantCulture) + " – " + b.ToString("0.000", CultureInfo.InvariantCulture) + " s ("
               + ((to - from) * 1000.0 / _frequency).ToString("0.0", CultureInfo.InvariantCulture) + " ms)";
    }
}
