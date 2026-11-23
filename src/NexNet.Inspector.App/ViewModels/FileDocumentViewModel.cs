using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// One session of a recording. The file is indexed on a background thread; while it is, views show what was read so
/// far and skeleton rows for the rest (OQ2), from <see cref="IndexingState"/> and <see cref="IndexingProgress"/>.
/// A damaged or footer-less file still opens: <see cref="Warnings"/> drives the warning banner (OQ3).
/// </summary>
public sealed partial class FileDocumentViewModel : DocumentViewModel
{
    private readonly CancellationTokenSource _cancel = new();
    private int _indexed;
    private int _total;
    private int _state;

    internal FileDocumentViewModel(RecordingFile recording, CaptureSession session, IUiDispatcher ui)
        : base(DocumentKind.File, ui)
    {
        Recording = recording;
        Session = session;
        Warnings = recording.Warnings;
        Title = recording.Path is { } path ? System.IO.Path.GetFileName(path) : "Recording";
        if (recording.Sessions.Count > 1)
            Title += $" · {LiveDocumentViewModel.SessionLabel(session.SessionId)}";
    }

    /// <summary>The recording.</summary>
    public RecordingFile Recording { get; }

    /// <summary>The file's full path, when it came from one.</summary>
    public string? Path => Recording.Path;

    /// <summary>What the warning banner says is wrong with the file (index rebuilt by a scan, damaged regions).</summary>
    public RecordingWarnings Warnings { get; }

    /// <summary>True when the warning banner shows.</summary>
    public bool HasWarnings => Warnings != RecordingWarnings.None;

    /// <summary>Damaged regions the reader skipped.</summary>
    public IReadOnlyList<NntlDamage> Damage => Recording.Damage;

    /// <summary>Where the background index stands.</summary>
    [ObservableProperty]
    public partial IndexingState IndexingState { get; private set; }

    /// <summary>Done fraction of the background index, 0 to 1.</summary>
    [ObservableProperty]
    public partial double IndexingProgress { get; private set; }

    /// <summary>True while the background index runs (views show skeleton rows past the indexed data).</summary>
    public bool IsIndexing => IndexingState == IndexingState.Indexing;

    /// <summary>Completes when the background index ends, however it ends.</summary>
    public Task Indexing { get; private set; } = Task.CompletedTask;

    /// <summary>Starts the background index.</summary>
    internal void StartIndexing()
    {
        if (Volatile.Read(ref _state) != (int)IndexingState.NotStarted)
            return;
        Volatile.Write(ref _state, (int)IndexingState.Indexing);
        IndexingState = IndexingState.Indexing;
        Indexing = RunIndexAsync();
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await _cancel.CancelAsync().ConfigureAwait(false);
        try
        {
            await Indexing.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The index's outcome no longer matters once the document is gone.
        }

        _cancel.Dispose();
        Recording.Dispose();
    }

    /// <inheritdoc/>
    protected override void OnRefresh()
    {
        var total = Volatile.Read(ref _total);
        IndexingProgress = total == 0 ? (IndexingState == IndexingState.Complete ? 1 : 0) : (double)Volatile.Read(ref _indexed) / total;
        IndexingState = (IndexingState)Volatile.Read(ref _state);
        OnPropertyChanged(nameof(IsIndexing));
    }

    private async Task RunIndexAsync()
    {
        var outcome = IndexingState.Complete;
        try
        {
            await Recording.IndexAsync(Session!, OnProgress, _cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = IndexingState.Canceled;
        }
        catch (Exception e) when (e is IOException or NntlFormatException or ObjectDisposedException)
        {
            outcome = IndexingState.Failed;
        }

        Volatile.Write(ref _state, (int)outcome);
        SignalChanged();
    }

    private void OnProgress(IndexingProgress progress)
    {
        Volatile.Write(ref _total, progress.TotalBlocks);
        Volatile.Write(ref _indexed, progress.Blocks);
        SignalChanged();
    }
}
