namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Represents the state of a stream transfer operation.
/// </summary>
public enum TransferState : byte
{
    /// <summary>
    /// Transfer is actively in progress.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Transfer is paused.
    /// </summary>
    Paused = 1,

    /// <summary>
    /// Transfer has completed successfully.
    /// </summary>
    Complete = 2,

    /// <summary>
    /// Transfer has failed.
    /// </summary>
    Failed = 3
}
