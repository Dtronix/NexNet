using NexNet;
using NexNet.Messages;
using NexNet.Pipes;
using NexNetSample.Asp.Shared;

namespace NexNetSample.Asp.Client;

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus
{
    public Action<ClientNexus>? ClientVoidEvent = null;
    public Action<ClientNexus, int>? ClientVoidWithParamEvent = null;
    public Func<ClientNexus, ValueTask>? ClientTaskEvent = null;
    public Func<ClientNexus, int, ValueTask>? ClientTaskWithParamEvent = null;
    public Func<ClientNexus, ValueTask<int>>? ClientTaskValueEvent = null;
    public Func<ClientNexus, int, ValueTask<int>>? ClientTaskValueWithParamEvent = null;
    public Func<ClientNexus, CancellationToken, ValueTask>? ClientTaskWithCancellationEvent = null;
    public Func<ClientNexus, int, CancellationToken, ValueTask>? ClientTaskWithValueAndCancellationEvent = null;
    public Func<ClientNexus, CancellationToken, ValueTask<int>>? ClientTaskValueWithCancellationEvent = null;
    public Func<ClientNexus, int, CancellationToken, ValueTask<int>>? ClientTaskValueWithValueAndCancellationEvent = null;
    public Func<ClientNexus, INexusDuplexPipe, ValueTask>? ClientTaskValueWithDuplexPipeEvent = null;
    public Func<ClientNexus, bool, ValueTask>? OnConnectedEvent = null;
    public Func<ClientNexus, ValueTask>? OnReconnectingEvent = null;
    public Func<ClientNexus, ValueTask>? OnDisconnectedEvent = null;

    public void ClientVoid()
    {
        ClientVoidEvent?.Invoke(this);
    }

    public void ClientVoidWithParam(int id)
    {
        this.ClientVoidWithParamEvent?.Invoke(this, id);
    }

    public ValueTask ClientTask()
    {
        if (ClientTaskEvent == null)
            throw new Exception();
        
        return ClientTaskEvent.Invoke(this);
    }

    public ValueTask ClientTaskWithParam(int data)
    {
        if (ClientTaskWithParamEvent == null)
            throw new Exception();
        
        return ClientTaskWithParamEvent.Invoke(this, data);
    }

    public ValueTask<int> ClientTaskValue()
    {
        if (ClientTaskValueEvent == null)
            throw new Exception();
        return ClientTaskValueEvent.Invoke(this);
    }

    public ValueTask<int> ClientTaskValueWithParam(int data)
    {
        if (ClientTaskValueWithParamEvent == null)
            throw new Exception();
        return ClientTaskValueWithParamEvent.Invoke(this, data);
    }

    public ValueTask ClientTaskWithCancellation(CancellationToken cancellationToken)
    {
        if (ClientTaskWithCancellationEvent == null)
            throw new Exception();
        
        return ClientTaskWithCancellationEvent.Invoke(this, cancellationToken);
    }

    public ValueTask ClientTaskWithValueAndCancellation(int value, CancellationToken cancellationToken)
    {
        if (ClientTaskWithValueAndCancellationEvent == null)
            throw new Exception();
        
        return ClientTaskWithValueAndCancellationEvent.Invoke(this, value, cancellationToken);
    }

    public ValueTask<int> ClientTaskValueWithCancellation(CancellationToken cancellationToken)
    {
        if (ClientTaskValueWithCancellationEvent == null)
            throw new Exception();
        
        return ClientTaskValueWithCancellationEvent.Invoke(this, cancellationToken);
    }

    public ValueTask<int> ClientTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken)
    {
        if (ClientTaskValueWithValueAndCancellationEvent == null)
            throw new Exception();
        
        return ClientTaskValueWithValueAndCancellationEvent.Invoke(this, value, cancellationToken);
    }

    public ValueTask ClientTaskValueWithDuplexPipe(INexusDuplexPipe pipe)
    {
        if (ClientTaskValueWithDuplexPipeEvent == null)
            throw new Exception();
        
        return ClientTaskValueWithDuplexPipeEvent.Invoke(this, pipe);
    }

    protected override ValueTask OnConnected(bool isReconnected)
    {
        if (OnConnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnConnectedEvent.Invoke(this, isReconnected);
    }

    protected override ValueTask OnDisconnected(DisconnectReason exception)
    {
        if (OnDisconnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnDisconnectedEvent.Invoke(this);
    }

    protected override ValueTask OnReconnecting()
    {
        if (OnReconnectingEvent == null)
            return ValueTask.CompletedTask;

        return OnReconnectingEvent.Invoke(this);
    }
}
