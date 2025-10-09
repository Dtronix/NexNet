using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes.Channels;

internal abstract class NexusDuplexChannelBase<T> : INexusDuplexChannel<T>
{
    protected INexusDuplexPipe? CoreBasePipe;

    public INexusDuplexPipe? BasePipe
    {
        get => CoreBasePipe;
    }

    public NexusDuplexChannelBase(INexusDuplexPipe coreBasePipe)
    {
        CoreBasePipe = coreBasePipe;
    }

    public ValueTask CompleteAsync()
    {
        return CoreBasePipe!.CompleteAsync();
    }

    public abstract ValueTask<INexusChannelWriter<T>> GetWriterAsync();

    public abstract ValueTask<INexusChannelReader<T>> GetReaderAsync();

    public ValueTask DisposeAsync()
    {
        var basePipe = Interlocked.Exchange(ref CoreBasePipe, null);
        if (basePipe == null)
            return ValueTask.CompletedTask;

        if (basePipe is IRentedNexusDuplexPipe rentedPipe)
        {
            return rentedPipe.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }
}
