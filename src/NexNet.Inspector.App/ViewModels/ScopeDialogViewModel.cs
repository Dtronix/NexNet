using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>What the Filter to time range… dialog scopes to.</summary>
public enum ScopeRangeKind : byte
{
    /// <summary>The time window the views show (the default).</summary>
    View = 0,

    /// <summary>The selected message's span, or the traced message's.</summary>
    Selection = 1,

    /// <summary>A start and end typed in milliseconds from the capture's start.</summary>
    Typed = 2,
}

/// <summary>
/// Filter to time range… (Ctrl+Shift+F, D2): sets the active document's scope from the current view (default), the
/// selected or traced message, or a typed start–end in ms. A document already scoped nests the new scope in its current
/// one (Back returns to it). Enter filters, Esc cancels.
/// </summary>
public sealed partial class ScopeDialogViewModel : ObservableObject
{
    private readonly DocumentsViewModel _documents;
    private readonly DocumentViewModel _document;
    private readonly TaskCompletionSource<TimeScope?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _origin;
    private readonly long _frequency;
    private readonly TimeWindow _window;
    private readonly TimeScope? _selection;
    private readonly TimeScope? _current;

    /// <param name="documents">The documents: the sync bus and the scope operations.</param>
    /// <param name="document">The document to scope; it must hold a session.</param>
    public ScopeDialogViewModel(DocumentsViewModel documents, DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(document);
        _documents = documents;
        _document = document;
        CaptureSession session = document.Session ?? throw new ArgumentException("The document holds no session.", nameof(document));
        _frequency = Math.Max(1, document.Frequency);
        _current = document.Scope;
        var sync = documents.Sync;
        lock (session.Gate)
        {
            _origin = session.StartTicks;
            _window = sync.Window.IsEmpty ? new TimeWindow(session.StartTicks, Math.Max(session.EndTicks, session.StartTicks + 1)) : sync.Window;
            MessageTrace? trace = null;
            if (!sync.Selection.IsNone && sync.Selection.Kind is SelectionKind.Frame or SelectionKind.Message or SelectionKind.Entity
                && MessageTrace.Resolve(session, sync.Selection) is { IsActive: true } selected)
                trace = selected;
            else if (sync.Trace is { IsActive: true } traced)
                trace = traced;
            if (trace is not null)
                _selection = TimeScope.Of(Widen(new TimeWindow(trace.Start, trace.End)), trace.Title);
        }

        FromText = Milliseconds(_window.Start);
        ToText = Milliseconds(_window.End);
    }

    /// <summary>Raised once the dialog is done (filtered, or canceled): the host removes it.</summary>
    public event Action<ScopeDialogViewModel>? Closed;

    public string Title => "Filter to time range";

    public string Hint => "Enter to filter · Esc to cancel";

    /// <summary>The document scoped.</summary>
    public DocumentViewModel Document => _document;

    /// <summary>What the scope is made from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsView), nameof(IsSelection), nameof(IsTyped), nameof(Summary))]
    public partial ScopeRangeKind Range { get; set; }

    /// <summary>The current view radio.</summary>
    public bool IsView
    {
        get => Range == ScopeRangeKind.View;
        set
        {
            if (value) Range = ScopeRangeKind.View;
        }
    }

    /// <summary>The selection or traced message radio.</summary>
    public bool IsSelection
    {
        get => Range == ScopeRangeKind.Selection;
        set
        {
            if (value && HasSelection) Range = ScopeRangeKind.Selection;
        }
    }

    /// <summary>The typed range radio.</summary>
    public bool IsTyped
    {
        get => Range == ScopeRangeKind.Typed;
        set
        {
            if (value) Range = ScopeRangeKind.Typed;
        }
    }

    /// <summary>True when a message is selected or traced (its radio is enabled).</summary>
    public bool HasSelection => _selection is not null;

    /// <summary>"1,240.0–1,290.0 ms": the view radio's hint.</summary>
    public string ViewText => TimeWindowReadoutViewModel.FormatScope(_window, _origin, _frequency);

    /// <summary>"Invocation #812 · 1,258.1–1,273.4 ms", or "nothing selected or traced".</summary>
    public string SelectionText => _selection is { } s
        ? s.Label + " · " + TimeWindowReadoutViewModel.FormatScope(s.Window, _origin, _frequency)
        : "nothing selected or traced";

    /// <summary>True when the document is scoped already: the new scope nests in it.</summary>
    public bool IsNested => _current is not null;

    /// <summary>The typed range's start, in ms from the capture's start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string FromText { get; set; }

    /// <summary>The typed range's end, in ms from the capture's start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string ToText { get; set; }

    /// <summary>"Every view shows 1,240.0–1,290.0 ms (50.0 ms)", "… inside the current scope" when nested.</summary>
    public string Summary => TryGetScope(out TimeScope scope, out _)
        ? "Every view shows " + TimeWindowReadoutViewModel.FormatScope(scope.Window, _origin, _frequency) + " ("
          + (scope.Duration * 1000.0 / _frequency).ToString("N1", CultureInfo.InvariantCulture) + " ms)"
          + (IsNested ? ", inside the current scope" : "")
        : "Type a start and an end in ms from the capture's start";

    /// <summary>Why the scope cannot be set; null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    /// <summary>True while the error line shows.</summary>
    public bool HasError => Error is not null;

    /// <summary>The scope set; null when canceled.</summary>
    public Task<TimeScope?> Result => _result.Task;

    /// <summary>The chosen scope in the session's ticks; false when the typed one does not parse.</summary>
    public bool TryGetScope(out TimeScope scope, out string? error)
    {
        error = null;
        switch (Range)
        {
            case ScopeRangeKind.Selection when _selection is { } s:
                scope = s;
                return true;
            case ScopeRangeKind.Typed:
                if (!ExportSectionDialogViewModel.TryParseMs(FromText, out double a) || !ExportSectionDialogViewModel.TryParseMs(ToText, out double b))
                {
                    scope = default;
                    error = "Type the start and end in ms, as the time column shows them (1240.5)";
                    return false;
                }

                if (b < a) (a, b) = (b, a);
                scope = TimeScope.Of(Widen(new TimeWindow(_origin + TimeWindow.TicksFromMilliseconds(a, _frequency),
                    _origin + TimeWindow.TicksFromMilliseconds(b, _frequency))), ScopeLabels.Typed);
                return true;
            default:
                scope = TimeScope.Of(_window, ScopeLabels.View);
                return true;
        }
    }

    partial void OnFromTextChanged(string value) => Error = null;

    partial void OnToTextChanged(string value) => Error = null;

    partial void OnRangeChanged(ScopeRangeKind value) => Error = null;

    /// <summary>Enter and Filter: sets the scope; the dialog closes once it is set.</summary>
    [RelayCommand]
    private void Apply()
    {
        if (_result.Task.IsCompleted) return;
        if (!TryGetScope(out TimeScope scope, out string? error))
        {
            Error = error;
            return;
        }

        if (!_documents.Documents.Contains(_document) || _documents.Active != _document)
        {
            Error = "The document is no longer the active one";
            return;
        }

        if (!_documents.PushScope(scope))
        {
            Error = "The range lies outside the current scope";
            return;
        }

        Finish(_document.Scope);
    }

    /// <summary>Esc and Cancel.</summary>
    [RelayCommand]
    private void Cancel() => Finish(null);

    private void Finish(TimeScope? scope)
    {
        if (!_result.TrySetResult(scope)) return;
        Closed?.Invoke(this);
    }

    // A scope is at least 10 µs wide (a single frame's instant widens around itself).
    private TimeWindow Widen(TimeWindow window)
    {
        long min = TimeWindow.MinSpan(_frequency);
        if (window.Duration >= min) return window;
        long start = window.Start + (window.Duration / 2) - (min / 2);
        return new TimeWindow(start, start + min);
    }

    private string Milliseconds(long ticks) =>
        TimeWindow.MillisecondsFromTicks(ticks - _origin, _frequency).ToString("0.000", CultureInfo.InvariantCulture);
}
