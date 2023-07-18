using Newtonsoft.Json.Linq;
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
    ValueTask ClientTaskValueWithDuplexPipe(INexusDuplexPipe pipe);
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
    ValueTask ServerTaskValueWithDuplexPipe(INexusDuplexPipe pipe);

    void ServerData(byte[] data);
}

/// <summary>
/// Nexus used for handling all Client communications.
/// </summary>
partial class ClientNexus : global::NexNet.Invocation.ClientNexusBase<global::NexNet.IntegrationTests.TestInterfaces.ClientNexus.ServerProxy>, global::NexNet.IntegrationTests.TestInterfaces.IClientNexus, global::NexNet.Invocation.IInvocationMethodHash
{
    /// <summary>
    /// Creates an instance of the client for this nexus and matching server.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="nexus">Nexus used for this client while communicating with the server. Useful to pass parameters to the nexus.</param>
    /// <returns>NexusClient for connecting to the matched NexusServer.</returns>
    public static global::NexNet.NexusClient<global::NexNet.IntegrationTests.TestInterfaces.ClientNexus, global::NexNet.IntegrationTests.TestInterfaces.ClientNexus.ServerProxy> CreateClient(global::NexNet.Transports.ClientConfig config, ClientNexus nexus)
    {
        return new global::NexNet.NexusClient<global::NexNet.IntegrationTests.TestInterfaces.ClientNexus, global::NexNet.IntegrationTests.TestInterfaces.ClientNexus.ServerProxy>(config, nexus);
    }

    protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
    {
        global::System.Threading.CancellationTokenSource? cts = null;
        global::NexNet.INexusDuplexPipe? duplexPipe = null;
        var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<global::NexNet.IntegrationTests.TestInterfaces.ClientNexus.ServerProxy>>(this);
        try
        {
            switch (message.MethodId)
            {
                case 0:
                    {
                        // void ClientVoid()
                        ClientVoid();
                        break;
                    }
                case 1:
                    {
                        // void ClientVoidWithParam(int id)
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                        ClientVoidWithParam(arguments.Item1);
                        break;
                    }
                case 2:
                    {
                        // ValueTask ClientTask()
                        await ClientTask();
                        break;
                    }
                case 3:
                    {
                        // ValueTask ClientTaskWithParam(int data)
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                        await ClientTaskWithParam(arguments.Item1);
                        break;
                    }
                case 4:
                    {
                        // ValueTask<ValueTask<int>> ClientTaskValue()
                        var result = await ClientTaskValue();
                        if (returnBuffer != null)
                            global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                        break;
                    }
                case 5:
                    {
                        // ValueTask<ValueTask<int>> ClientTaskValueWithParam(int data)
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                        var result = await ClientTaskValueWithParam(arguments.Item1);
                        if (returnBuffer != null)
                            global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                        break;
                    }
                case 6:
                    {
                        // ValueTask ClientTaskWithCancellation(CancellationToken cancellationToken)
                        cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                        await ClientTaskWithCancellation(cts.Token);
                        break;
                    }
                case 7:
                    {
                        // ValueTask ClientTaskWithValueAndCancellation(int value, CancellationToken cancellationToken)
                        cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                        await ClientTaskWithValueAndCancellation(arguments.Item1, cts.Token);
                        break;
                    }
                case 8:
                    {
                        // ValueTask<ValueTask<int>> ClientTaskValueWithCancellation(CancellationToken cancellationToken)
                        cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                        var result = await ClientTaskValueWithCancellation(cts.Token);
                        if (returnBuffer != null)
                            global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                        break;
                    }
                case 9:
                    {
                        // ValueTask<ValueTask<int>> ClientTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken)
                        cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                        var result = await ClientTaskValueWithValueAndCancellation(arguments.Item1, cts.Token);
                        if (returnBuffer != null)
                            global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                        break;
                    }
                case 10:
                    {
                        // ValueTask ClientTaskValueWithDuplexPipe(INexusDuplexPipe pipe)
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Byte>>();
                        duplexPipe = await methodInvoker.RegisterDuplexPipe(arguments.Item1);
                        await ClientTaskValueWithDuplexPipe(duplexPipe);
                        break;
                    }
            }
        }
        finally
        {
            if (cts != null)
            {
                methodInvoker.ReturnCancellationToken(message.InvocationId);
            }

            if (duplexPipe != null)
            {
                await methodInvoker.ReturnDuplexPipe(duplexPipe);
            }
        }

    }

    /// <summary>
    /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
    /// </summary>
    static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => -656302922; }

    /// <summary>
    /// Proxy invocation implementation for the matching nexus.
    /// </summary>
    public class ServerProxy : global::NexNet.Invocation.ProxyInvocationBase, global::NexNet.IntegrationTests.TestInterfaces.IServerNexus, global::NexNet.Invocation.IInvocationMethodHash
    {
        public void ServerVoid()
        {
            _ = ProxyInvokeMethodCore(0, null, InvocationFlags.None);
        }
        public void ServerVoidWithParam(global::System.Int32 id)
        {
            var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<global::System.Int32>>(new(id));
            _ = ProxyInvokeMethodCore(1, arguments, InvocationFlags.None);
        }
        public global::System.Threading.Tasks.ValueTask ServerTask()
        {
            return ProxyInvokeAndWaitForResultCore(2, null, null);
        }
        public global::System.Threading.Tasks.ValueTask ServerTaskWithParam(global::System.Int32 data)
        {
            var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<global::System.Int32>>(new(data));
            return ProxyInvokeAndWaitForResultCore(3, arguments, null);
        }
        public global::System.Threading.Tasks.ValueTask<global::System.Int32> ServerTaskValue()
        {
            return ProxyInvokeAndWaitForResultCore<global::System.Int32>(4, null, null);
        }
        public global::System.Threading.Tasks.ValueTask<global::System.Int32> ServerTaskValueWithParam(global::System.Int32 data)
        {
            var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<global::System.Int32>>(new(data));
            return ProxyInvokeAndWaitForResultCore<global::System.Int32>(5, arguments, null);
        }
        public global::System.Threading.Tasks.ValueTask ServerTaskWithCancellation(global::System.Threading.CancellationToken cancellationToken)
        {
            return ProxyInvokeAndWaitForResultCore(6, null, cancellationToken);
        }
        public global::System.Threading.Tasks.ValueTask ServerTaskWithValueAndCancellation(global::System.Int32 value, global::System.Threading.CancellationToken cancellationToken)
        {
            var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<global::System.Int32>>(new(value));
            return ProxyInvokeAndWaitForResultCore(7, arguments, cancellationToken);
        }
        public global::System.Threading.Tasks.ValueTask<global::System.Int32> ServerTaskValueWithCancellation(global::System.Threading.CancellationToken cancellationToken)
        {
            return ProxyInvokeAndWaitForResultCore<global::System.Int32>(8, null, cancellationToken);
        }
        public global::System.Threading.Tasks.ValueTask<global::System.Int32> ServerTaskValueWithValueAndCancellation(global::System.Int32 value, global::System.Threading.CancellationToken cancellationToken)
        {
            var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<global::System.Int32>>(new(value));
            return ProxyInvokeAndWaitForResultCore<global::System.Int32>(9, arguments, cancellationToken);
        }
        public global::System.Threading.Tasks.ValueTask ServerTaskValueWithDuplexPipe(global::NexNet.INexusDuplexPipe pipe)
        {
            var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<global::System.Byte>>(new(ProxyGetDuplexPipeInitialId(pipe)));
            return ProxyInvokeAndWaitForResultCore(10, arguments, null);
        }
        public void ServerData(global::System.Byte[] data)
        {
            var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<global::System.Byte[]>>(new(data));
            _ = ProxyInvokeMethodCore(11, arguments, InvocationFlags.None);
        }

        /// <summary>
        /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => -694753941; }
    }
}


//[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
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
    public Func<ServerNexus, INexusDuplexPipe, ValueTask> ServerTaskValueWithDuplexPipeEvent;

    public Action<ServerNexus, byte[]> ServerDataEvent;
    public Func<ServerNexus, ValueTask>? OnConnectedEvent;
    public Func<ServerNexus, ValueTask>? OnDisconnectedEvent;
    public Func<ServerNexus, ValueTask<IIdentity?>>? OnAuthenticateEvent;

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
    public ValueTask ServerTaskValueWithDuplexPipe(INexusDuplexPipe pipe)
    {
        return ServerTaskValueWithDuplexPipeEvent.Invoke(this, pipe);
    }

    public void ServerData(byte[] data)
    {
        ServerDataEvent.Invoke(this, data);
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

    protected override ValueTask<IIdentity?> OnAuthenticate(byte[]? authenticationToken)
    {
        return OnAuthenticateEvent!.Invoke(this);
    }
}
