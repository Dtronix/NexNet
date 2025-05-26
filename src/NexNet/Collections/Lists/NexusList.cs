using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Internals;
using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pipes;
using NexNetSample.Asp.Shared;

namespace NexNet.Collections.Lists;

public class NexusList<T>
{
    private readonly NexusDictionaryMode _mode;
    private readonly bool _server;
    private VersionedList<T> _itemList = new();
    private LockFreeArrayList<Client> _nexusPipeList;
    private static readonly Type _tType = typeof(T);
    private Client? _client;
    
    private record Client(
        INexusDuplexPipe Pipe, 
        INexusChannelReader<INexusListOperation>? Reader,
        INexusChannelWriter<INexusListOperation>? Writer,
        ISessionContext Context);

    internal NexusList(NexusDictionaryMode mode, bool server)
    {
        _mode = mode;
        _server = server;
        _nexusPipeList = new LockFreeArrayList<Client>(64);
    }

    public async ValueTask AddClient(INexusDuplexPipe pipe, ISessionContext context)
    {
        if(!_server)
            throw new InvalidOperationException("List is not setup in Server mode.");
        
        if (pipe.CompleteTask.IsCompleted)
            return;

        await pipe.ReadyTask;

        var writer = new NexusChannelWriter<INexusListOperation>(pipe);
        var op = NexusListAddItemOperation.GetFromCache();
        
        // TODO: Look at chunking

        var state = _itemList.CurrentState;
        foreach (var item in state.List)
        {
            op.Value = MemoryPackSerializer.Serialize(_tType, item);
            await writer.WriteAsync(op);
        }
        
        NexusListAddItemOperation.Cache.Add(op);
        
        _nexusPipeList.Add(new Client(
            pipe, 
            _mode == NexusDictionaryMode.BiDrirectional ? null : new NexusChannelReader<INexusListOperation>(pipe),
            writer,
            context));
        
        // Add in the completion removal for execution later.
        _ = pipe.CompleteTask.ContinueWith(static (saf, state )=>
        {
            var (pipe, list) = ((INexusDuplexPipe, LockFreeArrayList<INexusDuplexPipe>))state!;
            list.Remove(pipe);
        }, (pipe, _nexusPipeList), TaskContinuationOptions.RunContinuationsAsynchronously);
        
        await pipe.CompleteTask;
    }

    public void ConnectAsClient(INexusDuplexPipe pipe, ISessionContext context)
    {
        if(!_server)
            throw new InvalidOperationException("List is not setup in Client mode.");

        _client = new Client(
            pipe,
            new NexusChannelReader<INexusListOperation>(pipe),
            _mode == NexusDictionaryMode.BiDrirectional ? new NexusChannelWriter<INexusListOperation>(pipe) : null,
            context);
        
        Task.Factory.StartNew(async static state =>
        {
            var list = Unsafe.As<NexusList<T>>(state)!;

            await list._client!.Pipe.ReadyTask;

            await foreach (var operation in list._client!.Reader!)
            {
                var result = await ProcessOperation(list, operation);
                
                // If the result is false, close the whole pipe
                if (!result)
                {
                    await list._client.Context.DisconnectAsync(DisconnectReason.ProtocolError);
                    return;
                }
            }
        }, this, TaskCreationOptions.LongRunning);
        
        
    }

    private static async ValueTask<bool> ProcessOperation(NexusList<T> list, INexusListOperation operation)
    {
        switch (operation)
        {
            case NexusListAddItemOperation addOperation:
                if (list._server)
                    return false;
                
                
                break;
            case NexusListResetOperation resetOperation:
                if (list._server)
                    return false;
                
                break;
        }

        return false;
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
