namespace NexNet.Internals.Pipes;

internal enum NexusPipeBufferResult : byte
{
    Success,
    HighWatermarkReached,
    //HighCutoffReached,
    DataIgnored,
}
