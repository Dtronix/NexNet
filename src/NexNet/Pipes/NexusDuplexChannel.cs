using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes;

internal class NexusDuplexChannel<T> : INexusDuplexChannel<T>
{
    private INexusDuplexPipe? _basePipe;

    public INexusDuplexPipe? BasePipe
    {
        get => _basePipe;
    }

    public NexusDuplexChannel(INexusDuplexPipe basePipe)
    {
        _basePipe = basePipe;
    }

    public ValueTask CompleteAsync()
    {
        return _basePipe!.CompleteAsync();
    }

    public ValueTask<INexusChannelWriter<T>> GetWriterAsync()
    {
        return _basePipe!.GetChannelWriter<T>();
    }

    public ValueTask<INexusChannelReader<T>> GetReaderAsync()
    {
        return _basePipe!.GetChannelReader<T>();
    }

    public ValueTask DisposeAsync()
    {
        var basePipe = Interlocked.Exchange(ref _basePipe, null);
        if (basePipe == null)
            return ValueTask.CompletedTask;

        if (basePipe is IRentedNexusDuplexPipe rentedPipe)
        {
            return rentedPipe.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }
}
