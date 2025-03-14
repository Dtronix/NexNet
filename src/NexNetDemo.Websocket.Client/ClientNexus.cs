using NexNet;
using NexNet.Messages;
using NexNet.Pipes;
using NexNetDemo.Websocket.Shared;

namespace NexNetDemo.Websocket.Client;

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
public partial class ClientNexus
{
    public Action<ClientNexus> ClientVoidEvent;
    public Action<ClientNexus, int> ClientVoidWithParamEvent;
    public Func<ClientNexus, ValueTask> ClientTaskEvent;
    public Func<ClientNexus, int, ValueTask> ClientTaskWithParamEvent;
    public Func<ClientNexus, ValueTask<int>> ClientTaskValueEvent;
    public Func<ClientNexus, int, ValueTask<int>> ClientTaskValueWithParamEvent;
    public Func<ClientNexus, CancellationToken, ValueTask> ClientTaskWithCancellationEvent;
    public Func<ClientNexus, int, CancellationToken, ValueTask> ClientTaskWithValueAndCancellationEvent;
    public Func<ClientNexus, CancellationToken, ValueTask<int>> ClientTaskValueWithCancellationEvent;
    public Func<ClientNexus, int, CancellationToken, ValueTask<int>> ClientTaskValueWithValueAndCancellationEvent;
    public Func<ClientNexus, INexusDuplexPipe, ValueTask> ClientTaskValueWithDuplexPipeEvent;
    public Func<ClientNexus, bool, ValueTask>? OnConnectedEvent;
    public Func<ClientNexus, ValueTask>? OnReconnectingEvent;
    public Func<ClientNexus, ValueTask>? OnDisconnectedEvent;

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
        return ClientTaskEvent.Invoke(this);
    }

    public ValueTask ClientTaskWithParam(int data)
    {
        return ClientTaskWithParamEvent.Invoke(this, data);
    }

    public ValueTask<int> ClientTaskValue()
    {
        return ClientTaskValueEvent.Invoke(this);
    }

    public ValueTask<int> ClientTaskValueWithParam(int data)
    {
        return ClientTaskValueWithParamEvent.Invoke(this, data);
    }

    public ValueTask ClientTaskWithCancellation(CancellationToken cancellationToken)
    {
        return ClientTaskWithCancellationEvent.Invoke(this, cancellationToken);
    }

    public ValueTask ClientTaskWithValueAndCancellation(int value, CancellationToken cancellationToken)
    {
        return ClientTaskWithValueAndCancellationEvent.Invoke(this, value, cancellationToken);
    }

    public ValueTask<int> ClientTaskValueWithCancellation(CancellationToken cancellationToken)
    {
        return ClientTaskValueWithCancellationEvent.Invoke(this, cancellationToken);
    }

    public ValueTask<int> ClientTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken)
    {
        return ClientTaskValueWithValueAndCancellationEvent.Invoke(this, value, cancellationToken);
    }

    public ValueTask ClientTaskValueWithDuplexPipe(INexusDuplexPipe pipe)
    {
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
