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

internal class NexusList<T> : INexusList<T>, INexusCollectionConnector
{
    private readonly ushort _id;
    private readonly NexusCollectionMode _mode;
    private readonly bool _isServer;
    private VersionedList<T> _itemList = new();
    private LockFreeArrayList<Client> _nexusPipeList;
    private static readonly Type _tType = typeof(T);
    private Client? _client;
    
    private record Client(
        INexusDuplexPipe Pipe, 
        INexusChannelReader<INexusListOperation>? Reader,
        INexusChannelWriter<INexusListOperation>? Writer,
        INexusSession Session);

    public NexusList(ushort id, NexusCollectionMode mode, bool isServer)
    {
        _id = id;
        _mode = mode;
        _isServer = isServer;
        _nexusPipeList = new LockFreeArrayList<Client>(64);
    }

    public async ValueTask StartServerCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        if(!_isServer)
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
            _mode == NexusCollectionMode.BiDrirectional ? null : new NexusChannelReader<INexusListOperation>(pipe),
            writer,
            session));
        
        // Add in the completion removal for execution later.
        _ = pipe.CompleteTask.ContinueWith(static (saf, state )=>
        {
            var (pipe, list) = ((INexusDuplexPipe, LockFreeArrayList<INexusDuplexPipe>))state!;
            list.Remove(pipe);
        }, (pipe, _nexusPipeList), TaskContinuationOptions.RunContinuationsAsynchronously);
        
        await pipe.CompleteTask;
    }

    public async ValueTask ConnectAsClient(IProxyInvoker invoker, INexusSession session)
    {
        if(!_isServer)
            throw new InvalidOperationException("List is not setup in Client mode.");

        var pipe = session.PipeManager.RentPipe();

        if (pipe == null)
            throw new Exception("Could not instance new pipe.");
        
        // Invoke the method on the server to activate the pipe.
        invoker.Logger?.Log((invoker.Logger.Behaviors & Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? Logging.NexusLogLevel.Information : Logging.NexusLogLevel.Debug, invoker.Logger.Category, null, $"Connecting Proxy: ServerTaskValueWithDuplexPipe({_id});");
        await invoker.ProxyInvokeMethodCore(_id, new ValueTuple<Byte>(invoker.ProxyGetDuplexPipeInitialId(pipe)), InvocationFlags.DuplexPipe);

        await pipe.ReadyTask;
        
        _client = new Client(
            pipe,
            new NexusChannelReader<INexusListOperation>(pipe),
            _mode == NexusCollectionMode.BiDrirectional ? new NexusChannelWriter<INexusListOperation>(pipe) : null,
            session);
        
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
                    await list._client.Session.DisconnectAsync(DisconnectReason.ProtocolError);
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
                if (list._isServer)
                    return false;
                
                
                break;
            case NexusListResetOperation resetOperation:
                if (list._isServer)
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

    public Task ConnectAsync()
    {
        throw new NotImplementedException();
    }

    public Task DisconnectAsync()
    {
        throw new NotImplementedException();
    }
}

public interface INexusList<T> : INexusCollection
{
    void Clear();
    bool Contains(T item);
    void CopyTo(T[] array, int arrayIndex);
    bool Remove(T item);
    int Count { get; }
    bool IsReadOnly { get; }
    int IndexOf(T item);
    void Insert(int index, T item);
    void RemoveAt(int index);
    T this[int index] { get; set; }
    public Task ConnectAsync();
    public Task DisconnectAsync();
    
}

internal interface INexusCollectionConnector
{
    public ValueTask StartServerCollectionConnection(INexusDuplexPipe pipe, INexusSession context);
}

public interface INexusCollection
{
    
}
