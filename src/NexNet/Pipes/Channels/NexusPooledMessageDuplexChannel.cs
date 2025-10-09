using System.Threading.Tasks;
using MemoryPack;

namespace NexNet.Pipes.Channels;

internal class NexusPooledMessageDuplexChannel<TMessage> : NexusDuplexChannelBase<TMessage>
    where TMessage : NexusPooledMessageBase<TMessage>, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
{
    public NexusPooledMessageDuplexChannel(INexusDuplexPipe basePipe)
        : base(basePipe)
    {
    }
    
    public override ValueTask<INexusChannelWriter<TMessage>> GetWriterAsync()
    {
        return CoreBasePipe!.GetPooledMessageChannelWriter<TMessage>();
    }

    public override ValueTask<INexusChannelReader<TMessage>> GetReaderAsync()
    {
        return CoreBasePipe!.GetPooledMessageChannelReader<TMessage>();
    }
}
