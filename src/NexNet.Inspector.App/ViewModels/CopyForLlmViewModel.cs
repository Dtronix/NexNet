using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The "Copy for LLM ▸" submenu of the content and timeline menus (and the app menu's command): copies the selection,
/// the traced message, the current view or the scope as text (<see cref="LlmClip"/>). Made when a menu opens, so its
/// enabled items say what is there then.
/// </summary>
public sealed class CopyForLlmViewModel
{
    private readonly CaptureSession? _session;
    private readonly SyncBus _sync;
    private readonly Func<string, Task>? _copy;

    /// <param name="session">The active document's session; null disables every item.</param>
    /// <param name="sync">The sync bus: selection, trace, window and scope.</param>
    /// <param name="copy">Puts text on the clipboard; null disables every item.</param>
    public CopyForLlmViewModel(CaptureSession? session, SyncBus sync, Func<string, Task>? copy)
    {
        ArgumentNullException.ThrowIfNull(sync);
        _session = session;
        _sync = sync;
        _copy = copy;
    }

    /// <summary>The submenu's title.</summary>
    public static string Title => "Copy for LLM";

    /// <summary>True when <paramref name="kind"/> can be copied now.</summary>
    public bool CanCopy(LlmClipKind kind) => _copy is not null && LlmClip.IsAvailable(kind, _session, _sync);

    /// <summary>The text <paramref name="kind"/> copies; null when there is none.</summary>
    public string? Text(LlmClipKind kind) => _session is { } session && LlmClip.IsAvailable(kind, session, _sync)
        ? LlmClip.Build(kind, session, _sync)
        : null;

    /// <summary>Copies <paramref name="kind"/>'s text; false when there is nothing to copy.</summary>
    public async Task<bool> CopyAsync(LlmClipKind kind)
    {
        if (_copy is not { } copy || Text(kind) is not { } text) return false;
        await copy(text).ConfigureAwait(true);
        return true;
    }

    /// <summary>The app menu's command: the selection, else the traced message, else the current view.</summary>
    public Task<bool> CopyDefaultAsync() => CopyAsync(LlmClip.Default(_session, _sync));
}
