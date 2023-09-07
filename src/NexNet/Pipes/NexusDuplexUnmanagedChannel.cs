using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes;

internal class NexusDuplexUnmanagedChannel<T> : INexusDuplexUnmanagedChannel<T>
    where T : unmanaged
{
    private INexusDuplexPipe? _basePipe;

    public INexusDuplexPipe? BasePipe
    {
        get => _basePipe;
    }

    public NexusDuplexUnmanagedChannel(INexusDuplexPipe pipe)
    {
        _basePipe = pipe;
    }
    public ValueTask CompleteAsync()
    {
        return _basePipe!.CompleteAsync();
    }

    public ValueTask<INexusChannelWriter<T>> GetWriterAsync()
    {
        return _basePipe!.GetUnmanagedChannelWriter<T>();
    }

    public ValueTask<INexusChannelReader<T>> GetReaderAsync()
    {
        return _basePipe!.GetUnmanagedChannelReader<T>();
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
