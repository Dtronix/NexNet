using System;

namespace NexNet.Internals;

internal enum NexusPipeBufferResult : byte
{
    Success,
    HighWatermarkReached,
    HighCutoffReached,
    DataIgnored,
}
