using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Inspector;

/// <summary>The Chat fixture opened and indexed as a document, with the inspect-live screen's subjects picked.</summary>
internal static class ChatFixture
{
    public static async Task<(FileDocumentViewModel Document, InspectLiveScreen.Picks Picks)> OpenAsync(DocumentsViewModel documents, RecordingOpenChoice? choice = null)
    {
        FileDocumentViewModel document = (await documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl"), choice))!;
        await document.Indexing;
        await Wait.UntilAsync(() => document.IndexingState == IndexingState.Complete, "the index to complete");
        return (document, InspectLiveScreen.Pick(document));
    }
}
