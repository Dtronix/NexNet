using System;

namespace NexNet;

[Flags]
internal enum NexusDuplexPipeState : byte
{
    Unset = 0,
    ClientWriterComplete = 1 << 0,
    ClientReaderComplete = 1 << 1,
    ServerWriterComplete = 1 << 2,
    ServerReaderComplete = 1 << 3,
    ClientReady = 1 << 4,
    ServerReady = 1 << 5
}
