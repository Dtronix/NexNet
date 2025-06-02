namespace NexNet.Collections;

internal interface INexusCollectionMessage
{
    int Id { get; set; }
    void ReturnToCache();

    void CompleteBroadcast();

    int Remaining { get; set; }
}
