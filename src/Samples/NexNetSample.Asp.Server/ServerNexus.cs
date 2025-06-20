using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using NexNet;
using NexNet.Collections.Lists;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pipes;
using NexNetSample.Asp.Shared;

namespace NexNetSample.Asp.Server;

[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server, Versioning = NexusVersioning.Negotiation)]
public partial class ServerNexus
{
#pragma warning disable CS8618, CS9264
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
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
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    public Func<ServerNexus, byte[], ValueTask>? ServerDataEvent;
    public Func<ServerNexus, ValueTask>? OnConnectedEvent;
    public Func<ServerNexus, ValueTask>? OnDisconnectedEvent;
    public Func<ServerNexus, ValueTask<IIdentity?>>? OnAuthenticateEvent;

    public void ServerVoid()
    {
        ServerVoidEvent.Invoke(this);
    }

    public void ServerVoidWithParam(int id)
    {
        this.ServerVoidWithParamEvent.Invoke(this, id);
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
        return default;
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
     
     
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long CreateVerionHash(int version, ushort methodId)
        => ((long)version << 16) | methodId;
    
   
}

static file class Helpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long CreateVerionHash(int version, ushort methodId)
        => ((long)version << 16) | methodId;
}
