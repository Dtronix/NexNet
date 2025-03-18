using NexNet;
using NexNet.Messages;
using NexNet.Pipes;
using NexNetDemo.Websocket.Shared;

namespace NexNetDemo.Websocket.Server;

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
    public Func<ServerNexus, INexusDuplexPipe, ValueTask> ServerTaskValueWithDuplexPipeEvent;

    public Func<ServerNexus, byte[], ValueTask>? ServerDataEvent;
    public Func<ServerNexus, ValueTask>? OnConnectedEvent;
    public Func<ServerNexus, ValueTask>? OnDisconnectedEvent;
    public Func<ServerNexus, ValueTask<IIdentity?>>? OnAuthenticateEvent;

    public void ServerVoid()
    {
        ServerVoidEvent?.Invoke(this);
    }

    public void ServerVoidWithParam(int id)
    {
        this.ServerVoidWithParamEvent?.Invoke(this, id);
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
        return ValueTask.FromResult(++data);
        //return ServerTaskValueWithParamEvent.Invoke(this, data);
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

    public ValueTask ServerTaskValueWithDuplexPipe(INexusDuplexPipe pipe)
    {
        return ServerTaskValueWithDuplexPipeEvent.Invoke(this, pipe);
    }

    public ValueTask ServerData(byte[] data)
    {
        if (ServerDataEvent == null)
            return ValueTask.CompletedTask;

        return ServerDataEvent.Invoke(this, data);
    }

    protected override ValueTask OnConnected(bool isReconnected)
    {
        if (OnConnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnConnectedEvent.Invoke(this);
    }

    protected override ValueTask OnDisconnected(DisconnectReason exception)
    {
        if (OnDisconnectedEvent == null)
            return ValueTask.CompletedTask;

        return OnDisconnectedEvent.Invoke(this);
    }

    protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? authenticationToken)
    {
        return OnAuthenticateEvent!.Invoke(this);
    }
}
