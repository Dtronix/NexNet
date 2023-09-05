using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MemoryPack;

namespace NexNet;

internal  class NexusChannelReaderUnmanaged<T> : ChannelReader<T> 
    where T : unmanaged
{
    private readonly INexusDuplexPipe _duplexPipe;
    private readonly int _tSize;
    private readonly MemoryPackReaderOptionalState _readerState;

    public unsafe NexusChannelReaderUnmanaged(INexusDuplexPipe duplexPipe)
    {
        _tSize = sizeof(T);
        _duplexPipe = duplexPipe;
        _readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
    }
    public override bool TryRead(out T item)
    {
        var readResult = _duplexPipe.Input.TryRead(out var result);

        // Check if the result is completed or canceled.
        if (!readResult || result.IsCompleted || result.IsCanceled)
        {
            item = default;
            return false;
        }

        // Check to see if we have a enough data to read the type.
        if (result.Buffer.Length < _tSize)
        {
            item = default;
            return false;
        }

        var reader = new MemoryPackReader(result.Buffer, _readerState);
        item = reader.ReadUnmanaged<T>();

        _duplexPipe.ReaderCore.AdvanceTo(reader.Consumed);
        return true;
    }

    public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        var result = await _duplexPipe.Input.ReadAtLeastAsync(_tSize, cancellationToken);

        return !result.IsCompleted && !result.IsCanceled;
    }
}
