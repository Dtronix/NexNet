using System.Runtime.CompilerServices;
using NexNet;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pipes;
using NexNetSample.Asp.Shared;

namespace NexNetSample.Asp.Client;

//[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
public partial class ClientNexus
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

/// <summary>
/// Nexus used for handling all Client communications.
/// </summary>
public partial class ClientNexus : global::NexNet.Invocation.ClientNexusBase<global::NexNetSample.Asp.Client.ClientNexus.ServerProxy>, global::NexNetSample.Asp.Shared.IClientNexus, global::NexNet.Invocation.IInvocationMethodHash, global::NexNet.Collections.ICollectionConfigurer
{
    public static void ConfigureCollections(IConfigureCollectionManager manager)
    {
        manager.ConfigureList<int>(100, NexusCollectionMode.BiDrirectional);
        manager.CompleteConfigure();
    }
    
    /// <summary>
    /// Creates an instance of the client for this nexus and matching server.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="nexus">Nexus used for this client while communicating with the server. Useful to pass parameters to the nexus.</param>
    /// <returns>NexusClient for connecting to the matched NexusServer.</returns>
    public static global::NexNet.NexusClient<global::NexNetSample.Asp.Client.ClientNexus, global::NexNetSample.Asp.Client.ClientNexus.ServerProxy> CreateClient(global::NexNet.Transports.ClientConfig config, ClientNexus nexus)
    {
        return new global::NexNet.NexusClient<global::NexNetSample.Asp.Client.ClientNexus, global::NexNetSample.Asp.Client.ClientNexus.ServerProxy>(config, nexus);
    }

    protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
    {
        global::System.Threading.CancellationTokenSource? cts = null;
        global::NexNet.Pipes.INexusDuplexPipe? duplexPipe = null;
        var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker>(this);
        try
        {
            switch (message.MethodId)
            {
                case 0:
                {
                    // void ClientVoid()
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientVoid();");
                    ClientVoid();
                    break;
                }
                case 1:
                {
                    // void ClientVoidWithParam(int id)
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientVoidWithParam(id = {arguments.Item1});");
                    ClientVoidWithParam(arguments.Item1);
                    break;
                }
                case 2:
                {
                    // ValueTask ClientTask()
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientTask();");
                    await ClientTask().ConfigureAwait(false);
                    break;
                }
                case 3:
                {
                    // ValueTask ClientTaskWithParam(int data)
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientTaskWithParam(data = {arguments.Item1});");
                    await ClientTaskWithParam(arguments.Item1).ConfigureAwait(false);
                    break;
                }
                case 4:
                {
                    // ValueTask<ValueTask<int>> ClientTaskValue()
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientTaskValue();");
                    var result = await ClientTaskValue().ConfigureAwait(false);
                    if (returnBuffer != null)
                        global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                    break;
                }
                case 5:
                {
                    // ValueTask<ValueTask<int>> ClientTaskValueWithParam(int data)
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientTaskValueWithParam(data = {arguments.Item1});");
                    var result = await ClientTaskValueWithParam(arguments.Item1).ConfigureAwait(false);
                    if (returnBuffer != null)
                        global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                    break;
                }
                case 6:
                {
                    // ValueTask ClientTaskWithCancellation(CancellationToken cancellationToken)
                    cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientTaskWithCancellation(cancellationToken = ct);");
                    await ClientTaskWithCancellation(cts.Token).ConfigureAwait(false);
                    break;
                }
                case 7:
                {
                    // ValueTask ClientTaskWithValueAndCancellation(int value, CancellationToken cancellationToken)
                    cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientTaskWithValueAndCancellation(value = {arguments.Item1}, cancellationToken = ct);");
                    await ClientTaskWithValueAndCancellation(arguments.Item1, cts.Token).ConfigureAwait(false);
                    break;
                }
                case 8:
                {
                    // ValueTask<ValueTask<int>> ClientTaskValueWithCancellation(CancellationToken cancellationToken)
                    cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientTaskValueWithCancellation(cancellationToken = ct);");
                    var result = await ClientTaskValueWithCancellation(cts.Token).ConfigureAwait(false);
                    if (returnBuffer != null)
                        global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                    break;
                }
                case 9:
                {
                    // ValueTask<ValueTask<int>> ClientTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken)
                    cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientTaskValueWithValueAndCancellation(value = {arguments.Item1}, cancellationToken = ct);");
                    var result = await ClientTaskValueWithValueAndCancellation(arguments.Item1, cts.Token).ConfigureAwait(false);
                    if (returnBuffer != null)
                        global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                    break;
                }
                case 10:
                {
                    // ValueTask ClientTaskValueWithDuplexPipe(INexusDuplexPipe pipe)
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Byte>>();
                    duplexPipe = await methodInvoker.RegisterDuplexPipe(arguments.Item1).ConfigureAwait(false);
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ClientTaskValueWithDuplexPipe(pipe = {arguments.Item1});");
                    await ClientTaskValueWithDuplexPipe(duplexPipe).ConfigureAwait(false);
                    break;
                }
            }
        }
        finally
        {
            if(cts!= null)
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
    static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 1326271065; }

    /// <summary>
    /// Proxy invocation implementation for the matching nexus.
    /// </summary>
    public class ServerProxy : global::NexNet.Invocation.ProxyInvocationBase, global::NexNetSample.Asp.Shared.IServerNexus, global::NexNet.Invocation.IInvocationMethodHash
    {
        public INexusList<int> IntegerList => Unsafe.As<IProxyInvoker>(this).ProxyGetConfiguredNexusList<int>(100);
        
        public void ServerVoid()
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerVoid();");
             _ = __proxyInvoker.ProxyInvokeMethodCore(0, null, global::NexNet.Messages.InvocationFlags.None);
         }
         public void ServerVoidWithParam(global::System.Int32 id)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(id);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerVoidWithParam(id = {__proxyInvocationArguments.Item1});");
             _ = __proxyInvoker.ProxyInvokeMethodCore(1, __proxyInvocationArguments, global::NexNet.Messages.InvocationFlags.None);
         }
         public global::System.Threading.Tasks.ValueTask ServerTask()
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerTask();");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore(2, null, null);
         }
         public global::System.Threading.Tasks.ValueTask ServerTaskWithParam(global::System.Int32 data)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(data);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerTaskWithParam(data = {__proxyInvocationArguments.Item1});");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore(3, __proxyInvocationArguments, null);
         }
         public global::System.Threading.Tasks.ValueTask<global::System.Int32> ServerTaskValue()
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerTaskValue();");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore<global::System.Int32>(4, null, null);
         }
         public global::System.Threading.Tasks.ValueTask<global::System.Int32> ServerTaskValueWithParam(global::System.Int32 data)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(data);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerTaskValueWithParam(data = {__proxyInvocationArguments.Item1});");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore<global::System.Int32>(5, __proxyInvocationArguments, null);
         }
         public global::System.Threading.Tasks.ValueTask ServerTaskWithCancellation(global::System.Threading.CancellationToken cancellationToken)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerTaskWithCancellation(cancellationToken);");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore(6, null, cancellationToken);
         }
         public global::System.Threading.Tasks.ValueTask ServerTaskWithValueAndCancellation(global::System.Int32 value, global::System.Threading.CancellationToken cancellationToken)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(value);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerTaskWithValueAndCancellation(value = {__proxyInvocationArguments.Item1}, cancellationToken);");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore(7, __proxyInvocationArguments, cancellationToken);
         }
         public global::System.Threading.Tasks.ValueTask<global::System.Int32> ServerTaskValueWithCancellation(global::System.Threading.CancellationToken cancellationToken)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerTaskValueWithCancellation(cancellationToken);");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore<global::System.Int32>(8, null, cancellationToken);
         }
         public global::System.Threading.Tasks.ValueTask<global::System.Int32> ServerTaskValueWithValueAndCancellation(global::System.Int32 value, global::System.Threading.CancellationToken cancellationToken)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(value);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerTaskValueWithValueAndCancellation(value = {__proxyInvocationArguments.Item1}, cancellationToken);");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore<global::System.Int32>(9, __proxyInvocationArguments, cancellationToken);
         }
         public global::System.Threading.Tasks.ValueTask ServerTaskValueWithDuplexPipe(global::NexNet.Pipes.INexusDuplexPipe pipe)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Byte>(__proxyInvoker.ProxyGetDuplexPipeInitialId(pipe));
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerTaskValueWithDuplexPipe(pipe = {__proxyInvocationArguments.Item1});");
             return __proxyInvoker.ProxyInvokeMethodCore(10, __proxyInvocationArguments, global::NexNet.Messages.InvocationFlags.DuplexPipe);
         }
         public global::System.Threading.Tasks.ValueTask ServerData(global::System.Byte[] data)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Byte[]>(data);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ServerData(data = {__proxyInvocationArguments.Item1});");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore(11, __proxyInvocationArguments, null);
         }

        /// <summary>
        /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => -681352088; }
    }
}
