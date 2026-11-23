using Avalonia.Controls;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The compare screen (PLAN §8): a client-side and a server-side Chat capture in Compare, aligned On event (session
/// ready). The package pairs the client side with server-all › 0x3A21; server-all's sessions run the light scenario, so
/// the full-scenario server capture stands in for it, against the variant client run (its first message changed and
/// one message added), which gives the Δ strip real differences. The changed SendMessage invocation (its text differs) is
/// selected: pinned under Invocations in both files, the diff band around it, and below it the field diff (differing rows
/// tinted) and the A ⇄ B hex with its Text field picked (ringed in both rows, differing bytes marked).
/// </summary>
public static class CompareScreen
{
    /// <summary>What the scenario opened and picked, for tests.</summary>
    public sealed record Picks(FileDocumentViewModel A, FileDocumentViewModel B, int Message);

    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        Open(viewModel);
        return new MainWindow { DataContext = viewModel };
    }

    /// <summary>Opens both fixtures, ticks them, shows Compare and selects the changed SendMessage. Pumps the UI thread while they load.</summary>
    public static Picks Open(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DocumentsViewModel documents = viewModel.Documents;
        FileDocumentViewModel a = OpenFixture(documents, "chat-client-variant.nntl");
        FileDocumentViewModel b = OpenFixture(documents, "chat-server.nntl");
        a.IsIncludedInCompare = true;
        b.IsIncludedInCompare = true;
        viewModel.Chrome.Workspace.Value = Workspace.Compare;
        InspectLiveScreen.Pump(() => viewModel.Compare.Session is not null);

        CompareWorkspaceViewModel compare = viewModel.Compare;
        int message = FindInvocation(compare.Session!, "SendMessage");
        compare.Select(message);
        FieldDiffViewModel fields = compare.DetailPanel.FieldDiff;
        if (fields.Rows.Diff is { } diff)
        {
            for (int row = 0; row < diff.Rows.Length; row++)
            {
                if (diff.Rows[row].Kind == FieldDiffKind.Changed && diff.Rows[row].Path.EndsWith(".Text", StringComparison.Ordinal))
                {
                    fields.SelectRow(row);
                    break;
                }
            }
        }

        compare.Rows.SetPlayhead(compare.Session!.Clock.AnchorTime);
        InspectLiveScreen.Pump(static () => true);
        return new Picks(a, b, message);
    }

    /// <summary>The first invocation of a method, preferring one whose sides differ.</summary>
    public static int FindInvocation(CompareSession session, string method)
    {
        ArgumentNullException.ThrowIfNull(session);
        int first = -1;
        ReadOnlySpan<EntityMatch> matches = session.Matches;
        for (int i = 0; i < matches.Length; i++)
        {
            if (matches[i].Key.Kind != CompareEntityKind.Invocation || session.GetLabel(i) != method) continue;
            if (matches[i].HasDifferences) return i;
            if (first < 0) first = i;
        }

        return first >= 0 ? first : throw new InvalidOperationException("the fixtures have no " + method);
    }

    private static FileDocumentViewModel OpenFixture(DocumentsViewModel documents, string name)
    {
        Task<FileDocumentViewModel?> opening = documents.OpenRecordingAsync(Wait.Fixture(name));
        InspectLiveScreen.Pump(() => opening.IsCompleted);
        FileDocumentViewModel document = opening.Result ?? throw new InvalidOperationException(name + " did not open");
        InspectLiveScreen.Pump(() => document.Indexing.IsCompleted && document.IndexingState == IndexingState.Complete);
        return document;
    }
}
