using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The Chat captures opened as documents and ticked for Compare: the variant client run (A) and the server side of the
/// main run (B), so the compare has content and presence differences as well as mirrored directions.
/// </summary>
internal sealed record CompareFixture(DocumentsViewModel Documents, ShellChromeViewModel Chrome, CompareWorkspaceViewModel Compare,
    FileDocumentViewModel A, FileDocumentViewModel B) : IAsyncDisposable
{
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
}
