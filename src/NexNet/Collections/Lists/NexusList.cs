using System;
using System.Diagnostics.CodeAnalysis;
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

internal abstract class NexusCollection<T, TBaseOperation> : INexusCollectionConnector
{
    protected readonly ushort Id;
    protected readonly NexusCollectionMode Mode;
    protected static readonly Type TType = typeof(T);

    protected readonly bool IsServer;
    
    private Client? _client;
    private IProxyInvoker? _invoker;
    private INexusSession? _session;
    
    private LockFreeArrayList<Client> _nexusPipeList;

    protected NexusCollection(ushort id, NexusCollectionMode mode, bool isServer)
    {
        Id = id;
        Mode = mode;
        IsServer = isServer;
        _nexusPipeList = new LockFreeArrayList<Client>(64);
    }

    private record Client(
        INexusDuplexPipe Pipe, 
        INexusChannelReader<TBaseOperation>? Reader,
        INexusChannelWriter<TBaseOperation>? Writer,
        INexusSession Session);
    
    public async ValueTask StartServerCollectionConnection(INexusDuplexPipe pipe, INexusSession session)
    {
        if(!IsServer)
            throw new InvalidOperationException("List is not setup in Server mode.");
        
        if (pipe.CompleteTask.IsCompleted)
            return;

        await pipe.ReadyTask;

        var writer = new NexusChannelWriter<TBaseOperation>(pipe);
        
        await InitializeNewClient(writer);
        
        _nexusPipeList.Add(new Client(
            pipe, 
            Mode == NexusCollectionMode.BiDrirectional ? null : new NexusChannelReader<TBaseOperation>(pipe),
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

    public async Task ConnectAsync()
    {
        // Connect on the server is a noop.
        if(IsServer)
            return;

        var pipe = _session.PipeManager.RentPipe();

        if (pipe == null)
            throw new Exception("Could not instance new pipe.");
        
        // Invoke the method on the server to activate the pipe.
        _invoker.Logger?.Log((_invoker.Logger.Behaviors & Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? Logging.NexusLogLevel.Information : Logging.NexusLogLevel.Debug, _invoker.Logger.Category, null, $"Connecting Proxy Collection[{Id}];");
        await _invoker.ProxyInvokeMethodCore(Id, new ValueTuple<Byte>(_invoker.ProxyGetDuplexPipeInitialId(pipe)), InvocationFlags.DuplexPipe);

        await pipe.ReadyTask;
        
        _client = new Client(
            pipe,
            new NexusChannelReader<TBaseOperation>(pipe),
            Mode == NexusCollectionMode.BiDrirectional ? new NexusChannelWriter<TBaseOperation>(pipe) : null,
            _session);
        
        Task.Factory.StartNew(async static state =>
        {
            var collection = Unsafe.As<NexusCollection<T, TBaseOperation>>(state)!;

            await collection._client!.Pipe.ReadyTask;

            await foreach (var operation in collection._client!.Reader!)
            {
                var result = await collection.ProcessOperation(operation);
                
                // If the result is false, close the whole pipe
                if (!result)
                {
                    await collection._client.Session.DisconnectAsync(DisconnectReason.ProtocolError);
                    return;
                }
            }
        }, this, TaskCreationOptions.LongRunning);
    }
    
    public async Task DisconnectAsync()
    {
        // Disconnect on the server is a noop.
        if(IsServer)
            return;
        
        var client = _client;
        if (client == null)
            return;

        await client.Pipe.CompleteAsync();
    }


    public void TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session)
    {
        _invoker = invoker;
        _session = session;
    }
    
    protected abstract ValueTask InitializeNewClient(NexusChannelWriter<TBaseOperation> writer);
    
    protected abstract ValueTask<bool> ProcessOperation(TBaseOperation operation);

}

internal class NexusList<T> : NexusCollection<T, INexusListOperation>, INexusList<T>
{
    private readonly VersionedList<T> _itemList = new();
    public int Count => _itemList.Count;
    public bool IsReadOnly => IsServer ? false : Mode != NexusCollectionMode.BiDrirectional;
    
    public NexusList(ushort id, NexusCollectionMode mode, bool isServer)
        : base(id, mode, isServer)
    {
        
    }

    protected override async ValueTask InitializeNewClient(NexusChannelWriter<INexusListOperation> writer)
    {
              
        var op = NexusListAddItemOperation.GetFromCache();
        var state = _itemList.CurrentState;
        foreach (var item in state.List)
        {
            op.Value = MemoryPackSerializer.Serialize(TType, item);
            await writer.WriteAsync(op);
        }
        
        NexusListAddItemOperation.Cache.Add(op);
    }

    protected override async ValueTask<bool> ProcessOperation(INexusListOperation operation)
    {
        switch (operation)
        {
            case NexusListAddItemOperation addOperation:
                if (IsServer)
                    return false;
                
                
                break;
            case NexusListResetOperation resetOperation:
                if (IsServer)
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

    
}

internal interface INexusCollectionConnector
{
    /// <summary>
    /// Server only
    /// </summary>
    /// <param name="pipe"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public ValueTask StartServerCollectionConnection(INexusDuplexPipe pipe, INexusSession context);
    
    /// <summary>
    /// Client Only
    /// </summary>
    /// <param name="invoker"></param>
    /// <param name="session"></param>
    void TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session);
}

public interface INexusCollection
{
    public Task ConnectAsync();
    public Task DisconnectAsync();
}
