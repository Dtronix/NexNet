namespace NexNet.Collections;

internal interface INexusCollectionMessage
{
    void ReturnToCache();

    void CompleteBroadcast();

    int Remaining { get; set; }
}
