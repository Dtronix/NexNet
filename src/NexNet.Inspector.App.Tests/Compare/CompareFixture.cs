using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The Chat captures opened as documents and ticked for Compare: the variant client run (A) and the server side of the
/// main run (B), so the compare has content and presence differences as well as mirrored directions. Its static members
/// compare the fixtures' sessions directly, opened and indexed once (the field diff and A ⇄ B hex only read them; their
/// files stay open because payloads are read from them): the client run against the variant run (first message changed,
/// one extra call), and against the server's view of the same run (no differences in the attachment).
/// </summary>
internal sealed record CompareFixture(DocumentsViewModel Documents, ShellChromeViewModel Chrome, CompareWorkspaceViewModel Compare,
    FileDocumentViewModel A, FileDocumentViewModel B) : IAsyncDisposable
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, CaptureSession> Sessions = [];

    public static async Task<CompareFixture> OpenAsync(bool show = true, string a = "chat-client-variant.nntl", string b = "chat-server.nntl")
    {
        DocumentsViewModel documents = new(new TestUi(), null);
        ShellChromeViewModel chrome = new(documents);
        CompareWorkspaceViewModel compare = new(documents, chrome);
        FileDocumentViewModel first = await OpenIndexedAsync(documents, a);
        FileDocumentViewModel second = await OpenIndexedAsync(documents, b);
        if (show)
        {
            first.IsIncludedInCompare = true;
            second.IsIncludedInCompare = true;
            chrome.Workspace.Value = Workspace.Compare;
        }

        return new CompareFixture(documents, chrome, compare, first, second);
    }

    public static async Task<FileDocumentViewModel> OpenIndexedAsync(DocumentsViewModel documents, string name)
    {
        FileDocumentViewModel document = await documents.OpenRecordingAsync(Wait.Fixture(name)) ?? throw new InvalidOperationException(name);
        await document.Indexing;
        await Wait.UntilAsync(() => document.IndexingState == IndexingState.Complete, name + " indexed");
        return document;
    }

    public async ValueTask DisposeAsync()
    {
        Compare.Dispose();
        await Documents.DisposeAsync();
    }

    /// <summary>Ticks every group of a category in the sidebar (invocation groups start unticked).</summary>
    public static void ShowGroups(CompareRowsModel rows, CompareCategory category)
    {
        (int first, int count) = rows.GroupsOf(category);
        for (int g = first; g < first + count; g++) rows.SetGroupShown(g, true);
    }

    /// <summary>The first session of a fixture file, indexed.</summary>
    public static CaptureSession Session(string name)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(name, out CaptureSession? session))
            {
                RecordingFile file = RecordingFile.Open(Wait.Fixture(name));
                session = file.CreateSession(file.Sessions[0].SessionId);
                file.Index(session);
                Sessions[name] = session;
            }

            return session;
        }
    }

    /// <summary>chat-client.nntl (A) against chat-client-variant.nntl (B).</summary>
    public static CompareSession Variant() => new(Session("chat-client.nntl"), Session("chat-client-variant.nntl"));

    /// <summary>chat-client.nntl (A) against chat-server.nntl (B): two views of one run.</summary>
    public static CompareSession Mirrored() => new(Session("chat-client.nntl"), Session("chat-server.nntl"));

    /// <summary>The variant's SendMessage calls in order: [0] changed in place, [1] only in B.</summary>
    public static EntityMatch[] Sends(CompareSession compare) =>
        compare.Matches.ToArray().Where(m => m.Key.Kind == CompareEntityKind.Invocation && compare.GetLabel(m.Index) == "SendMessage").ToArray();

    /// <summary>The first entity with the label and kind.</summary>
    public static EntityMatch First(CompareSession compare, string label, CompareEntityKind kind) =>
        compare.Matches.ToArray().First(m => m.Key.Kind == kind && compare.GetLabel(m.Index) == label);

    /// <summary>The changed leaf of the variant's first SendMessage: its Text field (index into the diff's rows).</summary>
    public static int TextRow(EntityFieldDiff diff)
    {
        ReadOnlySpan<FieldDiff> rows = diff.Rows;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Kind == FieldDiffKind.Changed && rows[i].Path.EndsWith(".Text", StringComparison.Ordinal))
                return i;
        }

        throw new InvalidOperationException("the changed message has no Text row");
    }
}
