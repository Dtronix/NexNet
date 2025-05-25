using System.Linq;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using NexNet.Pipes;
using NexNetSample.Asp.Shared;

namespace NexNet.Collections.Lists;

public class NexusList<T>
{
    private readonly NexusDictionaryMode _mode;
    private readonly bool _server;
    private VersionedList<T> _itemList = new();
    private LockFreeArrayList<Client> _nexusPipeList;
    private Type _tType
    
    private record Client(INexusDuplexPipe Pipe, INexusChannelReader<INexusListOperation> Reader, INexusChannelWriter<INexusListOperation> Writer);

    internal NexusList(NexusDictionaryMode mode, bool server)
    {
        _mode = mode;
        _server = server;
        _nexusPipeList = new LockFreeArrayList<Client>(64);
    }

    public async ValueTask AddClient(INexusDuplexPipe pipe)
    {
        if (pipe.CompleteTask.IsCompleted)
            return;

        await pipe.ReadyTask;

        var writer = new NexusChannelWriter<INexusListOperation>(pipe);
        var op = NexusListFillItemOperation.GetFromCache();
        var type = typeof(T);
        
        // TODO: Look at chunking
        foreach (var item in _itemList)
        {
            op.Value = MemoryPackSerializer.Serialize(type, item);
            await writer.WriteAsync(op);
        }
        
        _nexusPipeList.Add(new Client(
            pipe, 
            new NexusChannelReader<INexusListOperation>(pipe),
            writer));
        
        // Add in the completion removal for execution later..
        _ = pipe.CompleteTask.ContinueWith(static (saf, state )=>
        {
            var (pipe, list) = ((INexusDuplexPipe, LockFreeArrayList<INexusDuplexPipe>))state!;
            list.Remove(pipe);
        }, (pipe, _nexusPipeList), TaskContinuationOptions.RunContinuationsAsynchronously);
        
        await pipe.CompleteTask;
    }
    
    public void Clear()
    {
        throw new System.NotImplementedException();
    }

    public bool Contains(T item)
    {
        throw new System.NotImplementedException();
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        throw new System.NotImplementedException();
    }

    public bool Remove(T item)
    {
        throw new System.NotImplementedException();
    }

    public int Count { get; }
    public bool IsReadOnly { get; }
    public int IndexOf(T item)
    {
        throw new System.NotImplementedException();
    }

    public void Insert(int index, T item)
    {
        throw new System.NotImplementedException();
    }

    public void RemoveAt(int index)
    {
        throw new System.NotImplementedException();
    }

    public T this[int index]
    {
        get => throw new System.NotImplementedException();
        set => throw new System.NotImplementedException();
    }
}
