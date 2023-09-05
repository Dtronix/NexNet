using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexNet;

internal  class NexusChannelReaderSlim<T> : ChannelReader<T> 
    where T : unmanaged
{
    private readonly INexusDuplexPipe _duplexPipe;
    private readonly int _tSize;

    public unsafe NexusChannelReaderSlim(INexusDuplexPipe duplexPipe)
    {
        _tSize = sizeof(T);
        _duplexPipe = duplexPipe;
    }
    public override bool TryRead(out T item)
    {
        _duplexPipe.Input.TryRead()
        throw new NotImplementedException();
    }

    public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        var result = await _duplexPipe.Input.ReadAtLeastAsync(_tSize, cancellationToken);

        return !result.IsCompleted && !result.IsCanceled;
    }
}
