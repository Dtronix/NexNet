namespace NexNet.Pipes;

internal enum NexusPipeBufferResult : byte
{
    Success,
    HighWatermarkReached,
    //HighCutoffReached,
    DataIgnored,
}
