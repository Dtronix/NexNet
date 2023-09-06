using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MemoryPack;

namespace NexNet;

internal  class NexusChannelWriterUnmanaged<T>
    where T : unmanaged
{
    private readonly INexusDuplexPipe _duplexPipe;
    private readonly int _tSize;
    private readonly MemoryPackReaderOptionalState _readerState;

    public unsafe NexusChannelWriterUnmanaged(INexusDuplexPipe duplexPipe)
    {
        _tSize = sizeof(T);
        _duplexPipe = duplexPipe;
        _readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
    }

    public bool WriteAsync(T item)
    {
        if(_duplexPipe.WriterCore.PauseWriting)
            return false;

        return true;

    }
}
