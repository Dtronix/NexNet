using System.Threading.Tasks;

namespace NexNet.Pipes.Channels;

internal class NexusDuplexChannel<T> : NexusDuplexChannelBase<T>
{
    public NexusDuplexChannel(INexusDuplexPipe basePipe)
        : base(basePipe)
    {
    }
    
    public override ValueTask<INexusChannelWriter<T>> GetWriterAsync()
    {
        return CoreBasePipe!.GetChannelWriter<T>();
    }

    public override ValueTask<INexusChannelReader<T>> GetReaderAsync()
    {
        return CoreBasePipe!.GetChannelReader<T>();
    }
}
