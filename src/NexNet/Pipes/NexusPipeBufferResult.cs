namespace NexNet.Pipes;

internal enum NexusPipeBufferResult : byte
{
    Success,
    DataIgnored,

    /// <summary>
    /// The peer sent more than its credit: a protocol violation.
    /// </summary>
    CreditExceeded,
}
