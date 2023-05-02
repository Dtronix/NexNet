namespace NexNet.IntegrationTests.TestInterfaces;

public partial interface IClientHub
{
    void ClientVoid();
    void ClientVoidWithParam(int id);
    ValueTask ClientTask();
    ValueTask ClientTaskWithParam(int data);
    ValueTask<int> ClientTaskValue();
    ValueTask<int> ClientTaskValueWithParam(int data);
    ValueTask ClientTaskWithCancellation(CancellationToken cancellationToken);
    ValueTask ClientTaskWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask<int> ClientTaskValueWithCancellation(CancellationToken cancellationToken);
    ValueTask<int> ClientTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
}


public partial interface IServerHub
{
    void ServerVoid();
    void ServerVoidWithParam(int id);
    ValueTask ServerTask();
    ValueTask ServerTaskWithParam(int data);
    ValueTask<int> ServerTaskValue();
    ValueTask<int> ServerTaskValueWithParam(int data);
    ValueTask ServerTaskWithCancellation(CancellationToken cancellationToken);
    ValueTask ServerTaskWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask<int> ServerTaskValueWithCancellation(CancellationToken cancellationToken);
    ValueTask<int> ServerTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
}

[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
public partial class ClientHub
{
    public Action<ClientHub> ClientVoidEvent;
    public Action<ClientHub, int> ClientVoidWithParamEvent;
    public Func<ClientHub, ValueTask> ClientTaskEvent;
    public Func<ClientHub, int, ValueTask> ClientTaskWithParamEvent;
    public Func<ClientHub, ValueTask<int>> ClientTaskValueEvent;
    public Func<ClientHub, int, ValueTask<int>> ClientTaskValueWithParamEvent;
    public Func<ClientHub, CancellationToken, ValueTask> ClientTaskWithCancellationEvent;
    public Func<ClientHub, int, CancellationToken, ValueTask> ClientTaskWithValueAndCancellationEvent;
    public Func<ClientHub, CancellationToken, ValueTask<int>> ClientTaskValueWithCancellationEvent;
    public Func<ClientHub, int, CancellationToken, ValueTask<int>> ClientTaskValueWithValueAndCancellationEvent;
    public Func<ClientHub, bool, ValueTask>? OnConnectedEvent;
    public Func<ClientHub, ValueTask>? OnReconnectingEvent;
    public Func<ClientHub, ValueTask>? OnDisconnectedEvent;
    public TaskCompletionSource ConnectedTCS = new TaskCompletionSource();
    public TaskCompletionSource DisconnectedTCS = new TaskCompletionSource();

    public void ClientVoid()
    {
        ClientVoidEvent?.Invoke(this);
    }

    public void ClientVoidWithParam(int id)
    {
        this.
        ClientVoidWithParamEvent?.Invoke(this, id);
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
    
    protected override ValueTask OnConnected(bool isReconnected)
    {
        ConnectedTCS?.TrySetResult();
        if (OnConnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnConnectedEvent.Invoke(this, isReconnected);
    }

    protected override ValueTask OnDisconnected(DisconnectReasonException exception)
    {
        DisconnectedTCS?.TrySetResult();
        if (OnDisconnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnDisconnectedEvent.Invoke(this);
    }

    protected override ValueTask OnReconnecting()
    {
        if(OnReconnectingEvent == null)
            return ValueTask.CompletedTask;

        return OnReconnectingEvent.Invoke(this);
    }
}

[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
public partial class ServerHub
{
    public Action<ServerHub> ServerVoidEvent;
    public Action<ServerHub, int> ServerVoidWithParamEvent;
    public Func<ServerHub, ValueTask> ServerTaskEvent;
    public Func<ServerHub, int, ValueTask> ServerTaskWithParamEvent;
    public Func<ServerHub, ValueTask<int>> ServerTaskValueEvent;
    public Func<ServerHub, int, ValueTask<int>> ServerTaskValueWithParamEvent;
    public Func<ServerHub, CancellationToken, ValueTask> ServerTaskWithCancellationEvent;
    public Func<ServerHub, int, CancellationToken, ValueTask> ServerTaskWithValueAndCancellationEvent;
    public Func<ServerHub, CancellationToken, ValueTask<int>> ServerTaskValueWithCancellationEvent;
    public Func<ServerHub, int, CancellationToken, ValueTask<int>> ServerTaskValueWithValueAndCancellationEvent;
    public Func<ServerHub, ValueTask>? OnConnectedEvent;
    public Func<ServerHub, ValueTask>? OnDisconnectedEvent;
    public TaskCompletionSource ConnectedTCS = new TaskCompletionSource();
    public TaskCompletionSource DisconnectedTCS = new TaskCompletionSource();

    public void ServerVoid()
    {
        ServerVoidEvent?.Invoke(this);
    }

    public void ServerVoidWithParam(int id)
    {
        this.
            ServerVoidWithParamEvent?.Invoke(this, id);
    }

    public ValueTask ServerTask()
    {
        return ServerTaskEvent.Invoke(this);
    }

    public ValueTask ServerTaskWithParam(int data)
    {
        return ServerTaskWithParamEvent.Invoke(this, data);
    }

    public ValueTask<int> ServerTaskValue()
    {
        return ServerTaskValueEvent.Invoke(this);
    }

    public ValueTask<int> ServerTaskValueWithParam(int data)
    {
        return ServerTaskValueWithParamEvent.Invoke(this, data);
    }

    public ValueTask ServerTaskWithCancellation(CancellationToken cancellationToken)
    {
        return ServerTaskWithCancellationEvent.Invoke(this, cancellationToken);
    }

    public ValueTask ServerTaskWithValueAndCancellation(int value, CancellationToken cancellationToken)
    {
        return ServerTaskWithValueAndCancellationEvent.Invoke(this, value, cancellationToken);
    }

    public ValueTask<int> ServerTaskValueWithCancellation(CancellationToken cancellationToken)
    {
        return ServerTaskValueWithCancellationEvent.Invoke(this, cancellationToken);
    }

    public ValueTask<int> ServerTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken)
    {
        return ServerTaskValueWithValueAndCancellationEvent.Invoke(this, value, cancellationToken);
    }

    protected override ValueTask OnConnected(bool isReconnected)
    {
        ConnectedTCS?.TrySetResult();
        if (OnConnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnConnectedEvent.Invoke(this);
    }

    protected override ValueTask OnDisconnected(DisconnectReasonException exception)
    {
        DisconnectedTCS?.TrySetResult();
        if (OnDisconnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnDisconnectedEvent.Invoke(this);
    }
}
