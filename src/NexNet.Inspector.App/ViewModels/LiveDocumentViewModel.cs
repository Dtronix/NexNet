using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Tap;
using NexNet.Messages;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// A live attach to one session of an inspector endpoint. The attach stream is read on a background thread into a
/// <see cref="LiveCapture"/>; the view shows data from the attach on. When the session disconnects the data stays
/// (<see cref="LiveState.Ended"/>, the session-ended banner) and is unsaved until saved. A detach stops the stream and
/// keeps the data the same way (the banner says "Detached by you"); only Discard or a new attach drops it.
/// </summary>
public sealed partial class LiveDocumentViewModel : DocumentViewModel
{
    /// <summary>The tooltip of the disabled record controls (OQ4).</summary>
    public const string RecordingDisabledReason = "Recording disabled by server config";

    /// <summary>The session-ended strip's reason after the user detached.</summary>
    public const string DetachedReason = "Detached by you";

    // How long a detach waits for the endpoint to end the stream (its close block and footer) before dropping it.
    private static readonly TimeSpan DetachTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    private readonly NexusSchema? _workspace;
    private readonly TimeProvider _time;
    private AttachRun? _run;

    // The session version a save covered: the document is dirty again once the session changes past it.
    private long _savedVersion = -1;

    internal LiveDocumentViewModel(InspectorConnection connection, IUiDispatcher ui, NexusSchema? workspace, TimeProvider? time = null)
        : base(DocumentKind.Live, ui)
    {
        Connection = connection;
        _workspace = workspace;
        _time = time ?? TimeProvider.System;
        Title = connection.ServerInfo.Role == NntlSessionRole.Client ? "Client" : "Server";
    }

    /// <summary>The endpoint connection; the document owns it.</summary>
    public InspectorConnection Connection { get; }

    /// <summary>The endpoint's address.</summary>
    public EndpointAddress Endpoint => Connection.Address;

    /// <summary>The endpoint's policy.</summary>
    public InspectorServerInfo ServerInfo => Connection.ServerInfo;

    /// <summary>True when the inspected side allows recording; otherwise the record controls are disabled (OQ4).</summary>
    public bool RecordingAllowed => ServerInfo.AllowRecording;

    /// <summary>The record controls' tooltip when they are disabled; null when recording is allowed.</summary>
    public string? RecordingDisabledTooltip => RecordingAllowed ? null : RecordingDisabledReason;

    /// <summary>Where the attach stands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStreaming))]
    public partial LiveState State { get; private set; } = LiveState.Detached;

    /// <summary>The attached session as the endpoint listed it; null for a client target or before attaching.</summary>
    [ObservableProperty]
    public partial InspectorSessionInfo? AttachedSession { get; private set; }

    /// <summary>The attach request in use.</summary>
    [ObservableProperty]
    public partial InspectorAttachRequest? Request { get; private set; }

    /// <summary>When the current attach started streaming (the title bar's REC timer counts from it); null when detached.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? AttachedAt { get; private set; }

    /// <summary>Why the session ended, once it did (the banner's "DisconnectGraceful (21)").</summary>
    [ObservableProperty]
    public partial DisconnectReason? EndReason { get; private set; }

    /// <summary>When the session ended (the banner's attached time runs from <see cref="AttachedAt"/> to it); null while live.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? EndedAt { get; private set; }

    /// <summary>
    /// Which side sent the disconnect that ended the session ("client" or "server", the banner's "from client"); null when
    /// no disconnect frame was captured (the connection simply dropped).
    /// </summary>
    [ObservableProperty]
    public partial string? EndedBy { get; private set; }

    /// <summary>True once the user detached: the stream stopped, the data stays (the strip's "Detached by you").</summary>
    [ObservableProperty]
    public partial bool IsDetachedByUser { get; private set; }

    /// <summary>Where the data was last saved (Save .nntl…); null before a save.</summary>
    [ObservableProperty]
    public partial string? SavedPath { get; private set; }

    /// <summary>True while the attach streams: a save then covers only what arrived so far.</summary>
    public bool IsStreaming => State is LiveState.Live or LiveState.Attaching;

    /// <summary>Why the attach failed or the stream broke.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>A short label for a session id: the low 16 bits in hex, as the title bar shows it ("Session 0x3A21").</summary>
    public static string SessionLabel(long sessionId) => "Session " + SessionHex(sessionId);

    /// <summary>The low 16 bits of a session id in hex ("0x3A21"), as tabs and the session group show it.</summary>
    public static string SessionHex(long sessionId) =>
        "0x" + ((ushort)sessionId).ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>A live tab's title: "Live · 0x3A21" (document-tab).</summary>
    public static string TabTitle(long sessionId) => "Live · " + SessionHex(sessionId);

    /// <summary>
    /// Attaches (again) and starts reading the stream. A document that already holds a session drops it first: the
    /// viewer keeps only data since attach.
    /// </summary>
    /// <param name="request">Target, overflow and snapshot.</param>
    /// <param name="session">The session picked from the list, for display; null for a client target.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <exception cref="InspectorRequestException">The endpoint refused.</exception>
    public async Task AttachAsync(InspectorAttachRequest request, InspectorSessionInfo? session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await StopRunAsync().ConfigureAwait(true);
        Reset();
        Request = request;
        AttachedSession = session;
        Title = session is { } s ? TabTitle(s.Id) : ServerInfo.Role == NntlSessionRole.Client ? "Client" : "Server";
        State = LiveState.Attaching;

        InspectorStream stream;
        try
        {
            stream = await Connection.Client.AttachAsync(request, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception e) when (e is InspectorRequestException or InspectorConnectionException or IOException or OperationCanceledException)
        {
            Error = e.Message;
            State = LiveState.Failed;
            throw;
        }

        var run = new AttachRun(new LiveCapture(_workspace), stream);
        _run = run;
        AttachedAt = _time.GetUtcNow();
        State = LiveState.Live;
        run.Pump = Task.Run(() => PumpAsync(run));
    }

    /// <summary>
    /// Detaches: the endpoint ends the stream (the server stops sending to this viewer) and what was captured stays in
    /// the tab as ended, unsaved data, like a session that disconnected: the session-ended strip offers Save .nntl…,
    /// Attach to another session and Discard, and the view keeps its window and selection.
    /// </summary>
    [RelayCommand]
    public async Task DetachAsync()
    {
        if (_run is not { } run)
            return;

        run.Detaching = true;
        try
        {
            await Connection.Client.DetachAsync().WaitAsync(DetachTimeout).ConfigureAwait(true);

            // The endpoint ends the stream with the session's close block and the footer: read them, so the kept data
            // ends as stopped rather than cut off.
            if (run.Pump is { } pump)
                await pump.WaitAsync(DrainTimeout).ConfigureAwait(true);
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException or InspectorConnectionException)
        {
            // The endpoint is gone or slow; the stream is dropped below either way.
        }

        await StopRunAsync().ConfigureAwait(true);
        if (Session is null && run.Capture.Session is { } opened)
            Session = opened;
        EndReason = null;
        EndedBy = null;
        Error = null;
        EndedAt = _time.GetUtcNow();
        IsDetachedByUser = true;
        IsDirty = Session is { } s && s.OpCount > 0 && s.Version != _savedVersion;
        State = LiveState.Ended;

        // The views and the counts learn of the stopped capture like any other change.
        SignalChanged();
    }

    /// <summary>
    /// Marks the data saved (Save .nntl…), so closing no longer asks until more arrives after the save.
    /// </summary>
    /// <param name="path">Where it was saved.</param>
    /// <param name="version">The session's <see cref="CaptureSession.Version"/> when the save began.</param>
    internal void MarkSaved(string path, long version)
    {
        SavedPath = path;
        _savedVersion = version;
        IsDirty = Session is { } s && s.Version != version;
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await StopRunAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void OnRefresh()
    {
        if (_run is not { } run)
            return;

        if (Session is null && run.Capture.Session is { } opened)
            Session = opened;

        if (run.Done && !run.Detaching)
        {
            var session = Session;
            if (session?.Close is { Kind: NntlCloseKind.Disconnected } close)
            {
                EndReason = close.DisconnectReason;
                EndedBy = FindDisconnectSide(session);
                State = LiveState.Ended;
            }
            else
            {
                Error ??= run.Error ?? "The inspector endpoint stopped streaming.";
                State = session is null ? LiveState.Failed : LiveState.Ended;
            }

            EndedAt ??= _time.GetUtcNow();
        }

        IsDirty = Session is { } s && s.OpCount > 0 && s.Version != _savedVersion;
    }

    private async Task PumpAsync(AttachRun run)
    {
        try
        {
            while (await run.Stream.ReadAsync(run.Cancel.Token).ConfigureAwait(false))
            {
                run.Capture.Accept(run.Stream.Current);
                SignalChanged();
            }

            if (!run.Capture.IsEnded)
                run.Error = "The stream ended without its footer.";
        }
        catch (OperationCanceledException)
        {
            // Stopped by a detach or by closing the document.
        }
        catch (Exception e) when (e is NntlFormatException or IOException or InvalidOperationException)
        {
            run.Error = e.Message;
        }
        finally
        {
            // A session that never got its close block (the endpoint went away) ends truncated.
            run.Capture.Abandon();
            run.Done = true;
            SignalChanged();
        }
    }

    private async Task StopRunAsync()
    {
        if (_run is not { } run)
            return;

        _run = null;
        run.Detaching = true;
        await run.Cancel.CancelAsync().ConfigureAwait(false);
        if (run.Pump is { } pump)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // PumpAsync records its own failures; nothing else escapes it.
            }
        }

        await run.Stream.DisposeAsync().ConfigureAwait(false);
        run.Cancel.Dispose();
    }

    // The side whose disconnect frame ended the session: a disconnect the tapped endpoint read came from its peer.
    private static string? FindDisconnectSide(CaptureSession session)
    {
        lock (session.Gate)
        {
            var order = session.FrameOrder;
            // The disconnect is among the last frames; a few back covers a ping or write that raced it.
            for (int i = order.Length - 1, seen = 0; i >= 0 && seen < 16; i--, seen++)
            {
                if (!session.TryGetFrame(order[i], out var frame) || (byte)frame.Type is < 20 or > 39)
                    continue;
                var peer = frame.Direction == Core.Model.TapDirection.Inbound;
                return (session.Role == NntlSessionRole.Server) == peer ? "client" : "server";
            }
        }

        return null;
    }

    private void Reset()
    {
        Session = null;
        ResetCounts();
        EndReason = null;
        EndedAt = null;
        EndedBy = null;
        SavedPath = null;
        Error = null;
        IsDetachedByUser = false;
        IsDirty = false;
        _savedVersion = -1;
        SavedSelection = Selection.None;
    }

    private sealed class AttachRun(LiveCapture capture, InspectorStream stream)
    {
        public readonly LiveCapture Capture = capture;
        public readonly InspectorStream Stream = stream;
        public readonly CancellationTokenSource Cancel = new();
        public Task? Pump;
        public volatile bool Done;
        public volatile bool Detaching;
        public volatile string? Error;
    }
}
