using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;

namespace NexNet.Inspector.App.Model;

/// <summary>What "Copy for LLM" copies.</summary>
public enum LlmClipKind : byte
{
    /// <summary>The selected invocation, pipe or frame, decoded (`show`).</summary>
    Selection = 0,

    /// <summary>The traced message: the frames and ops that carried it (`trace`), or its time span's lines.</summary>
    Trace = 1,

    /// <summary>The lines of the time window the views show.</summary>
    View = 2,

    /// <summary>The lines of the active document's scope (D2).</summary>
    Scope = 3,
}

/// <summary>
/// "Copy for LLM" (content, timeline and app menus, D6-E): the selection, the traced message, the current view or the
/// scope of the active document, in the inspector's text format (Core <see cref="TextClip"/>: the session's header line
/// first, about 4k tokens at most, a budget line when cut), ready to paste into a chat with a model.
/// </summary>
public static class LlmClip
{
    /// <summary>Every kind, in menu order.</summary>
    public static IReadOnlyList<LlmClipKind> Kinds { get; } = [LlmClipKind.Selection, LlmClipKind.Trace, LlmClipKind.View, LlmClipKind.Scope];

    /// <summary>The submenu item's title.</summary>
    public static string Title(LlmClipKind kind) => kind switch
    {
        LlmClipKind.Selection => "Selection",
        LlmClipKind.Trace => "Traced message",
        LlmClipKind.View => "Current view",
        _ => "Scope",
    };

    /// <summary>True when <paramref name="kind"/> has something to copy now.</summary>
    public static bool IsAvailable(LlmClipKind kind, CaptureSession? session, SyncBus sync)
    {
        ArgumentNullException.ThrowIfNull(sync);
        if (session is null) return false;
        return kind switch
        {
            LlmClipKind.Selection => SelectionId(session, sync.Selection) is not null,
            LlmClipKind.Trace => sync.Trace.IsActive,
            LlmClipKind.View => true,
            _ => sync.IsScoped,
        };
    }

    /// <summary>What the app menu's command copies: the selection, else the traced message, else the current view.</summary>
    public static LlmClipKind Default(CaptureSession? session, SyncBus sync)
    {
        if (IsAvailable(LlmClipKind.Selection, session, sync)) return LlmClipKind.Selection;
        return IsAvailable(LlmClipKind.Trace, session, sync) ? LlmClipKind.Trace : LlmClipKind.View;
    }

    /// <summary>The text of <paramref name="kind"/>; null when it has nothing to copy.</summary>
    /// <param name="kind">What to copy.</param>
    /// <param name="session">The active document's session.</param>
    /// <param name="sync">The sync bus: selection, trace, window and scope.</param>
    /// <param name="maxTokens">The budget in approximate tokens.</param>
    public static string? Build(LlmClipKind kind, CaptureSession session, SyncBus sync, long maxTokens = TextClip.DefaultMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sync);
        TextOptions range = new() { MaxTokens = maxTokens };
        TextOptions entity = new() { MaxTokens = maxTokens, ValueWidth = 64, Depth = 8 };
        switch (kind)
        {
            case LlmClipKind.Selection:
                return SelectionId(session, sync.Selection) is { } id ? TextClip.Entity(session, id, TextClipDetail.Show, entity) : null;
            case LlmClipKind.Trace:
                MessageTrace trace = sync.Trace;
                if (!trace.IsActive) return null;
                if (TextClip.IdOf(session, trace.Entity, trace.EntityIndex) is { } traced
                    && TextClip.Entity(session, traced, TextClipDetail.Trace, entity) is { } text)
                    return text;
                return TextClip.Range(session, new TimeWindow(trace.Start, trace.End + 1), "trace " + trace.Title, range);
            case LlmClipKind.View:
                TimeWindow window = sync.Window;
                if (window.IsEmpty)
                {
                    lock (session.Gate)
                        window = new TimeWindow(session.StartTicks, Math.Max(session.EndTicks, session.StartTicks) + 1);
                }

                return TextClip.Range(session, window, "view", range);
            default:
                return sync.IsScoped ? TextClip.Range(session, sync.Scope, "scope", range) : null;
        }
    }

    /// <summary>
    /// The query ID of what is selected: an invocation or pipe (`#5`, `≋4·4`), else the selected frame (`f18`); null
    /// for nothing, a byte range or a greeting.
    /// </summary>
    public static string? SelectionId(CaptureSession session, Selection selection)
    {
        ArgumentNullException.ThrowIfNull(session);
        switch (selection.Kind)
        {
            case SelectionKind.Entity:
                return TextClip.IdOf(session, selection.Entity, selection.EntityIndex);
            case SelectionKind.Message:
                MessageTrace trace;
                lock (session.Gate)
                    trace = MessageTrace.Resolve(session, selection);
                return TextClip.IdOf(session, trace.Entity, trace.EntityIndex) ?? TextClip.IdOf(session, selection.Frame);
            case SelectionKind.Frame:
                return TextClip.IdOf(session, selection.Frame);
            default:
                return null;
        }
    }
}
