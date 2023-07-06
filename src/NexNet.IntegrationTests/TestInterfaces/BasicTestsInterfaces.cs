﻿using Newtonsoft.Json.Linq;
using System.Threading;
using NexNet.Messages;
// ReSharper disable InconsistentNaming
#pragma warning disable CS8618
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.TestInterfaces;

public partial interface IClientNexus
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

    ValueTask ClientTask(NexusPipe pipeTest);
    ValueTask ClientTask(int test, NexusPipe pipeTest);
    ValueTask ClientTask(int test, NexusPipe pipeTest, int test2);
    ValueTask ClientTask(int test, NexusPipe pipeTest, int test2, CancellationToken cancellationToken);

}



public partial interface IServerNexus
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

    void ServerData(byte[] data);
}

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
    public Func<ClientNexus, bool, ValueTask>? OnConnectedEvent;
    public Func<ClientNexus, ValueTask>? OnReconnectingEvent;
    public Func<ClientNexus, ValueTask>? OnDisconnectedEvent;
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

    public ValueTask ClientTask(NexusPipe pipeTest)
    {
        throw new NotImplementedException();
    }

    public ValueTask ClientTask(int test, NexusPipe pipeTest)
    {
        throw new NotImplementedException();
    }

    public ValueTask ClientTask(int test, NexusPipe pipeTest, int test2)
    {
        throw new NotImplementedException();
    }

    public ValueTask ClientTask(int test, NexusPipe pipeTest, int test2, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
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

    protected override ValueTask OnDisconnected(DisconnectReason exception)
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

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
public partial class ServerNexus
{
    public Action<ServerNexus> ServerVoidEvent;
    public Action<ServerNexus, int> ServerVoidWithParamEvent;
    public Func<ServerNexus, ValueTask> ServerTaskEvent;
    public Func<ServerNexus, int, ValueTask> ServerTaskWithParamEvent;
    public Func<ServerNexus, ValueTask<int>> ServerTaskValueEvent;
    public Func<ServerNexus, int, ValueTask<int>> ServerTaskValueWithParamEvent;
    public Func<ServerNexus, CancellationToken, ValueTask> ServerTaskWithCancellationEvent;
    public Func<ServerNexus, int, CancellationToken, ValueTask> ServerTaskWithValueAndCancellationEvent;
    public Func<ServerNexus, CancellationToken, ValueTask<int>> ServerTaskValueWithCancellationEvent;
    public Func<ServerNexus, int, CancellationToken, ValueTask<int>> ServerTaskValueWithValueAndCancellationEvent;
    public Action<ServerNexus, byte[]> ServerDataEvent;
    public Func<ServerNexus, ValueTask>? OnConnectedEvent;
    public Func<ServerNexus, ValueTask>? OnDisconnectedEvent;
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
    public void ServerData(byte[] data)
    {
        ServerDataEvent.Invoke(this, data);
    }

    protected override ValueTask OnConnected(bool isReconnected)
    {
        ConnectedTCS?.TrySetResult();
        if (OnConnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnConnectedEvent.Invoke(this);
    }

    protected override ValueTask OnDisconnected(DisconnectReason exception)
    {
        DisconnectedTCS?.TrySetResult();
        if (OnDisconnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnDisconnectedEvent.Invoke(this);
    }
}
