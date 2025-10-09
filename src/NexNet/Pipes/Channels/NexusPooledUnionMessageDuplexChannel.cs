using System.Threading.Tasks;

namespace NexNet.Pipes.Channels;

internal class NexusPooledUnionMessageDuplexChannel<TUnion> : NexusDuplexChannelBase<TUnion>
    where TUnion : class, INexusPooledMessageUnion<TUnion>
{
    public NexusPooledUnionMessageDuplexChannel(INexusDuplexPipe basePipe)
        : base(basePipe)
    {
    }
    
    public override ValueTask<INexusChannelWriter<TUnion>> GetWriterAsync()
    {
        return CoreBasePipe!.GetPooledUnionMessageChannelWriter<TUnion>();
    }

    public override ValueTask<INexusChannelReader<TUnion>> GetReaderAsync()
    {
        return CoreBasePipe!.GetPooledUnionMessageChannelReader<TUnion>();
    }
}
