using System.Runtime.CompilerServices;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Invocation;

namespace NexNetSample.Asp.Server;

partial class ServerNexus : global::NexNet.Invocation.ServerNexusBase<global::NexNetSample.Asp.Server.ServerNexus.ClientProxy>, global::NexNetSample.Asp.Shared.IServerNexus, global::NexNet.Invocation.IInvocationMethodHash, global::NexNet.Collections.ICollectionConfigurer 
{
    public INexusList<int> IntegerList => Unsafe.As<ICollectionStore>(this).GetList<int>(100);
    
    public static void ConfigureCollections(IConfigureCollectionManager manager)
    {
        manager.ConfigureList<int>(100, NexusCollectionMode.BiDrirectional);
        manager.CompleteConfigure();
    }
    
    /// <summary>
    /// Creates an instance of the server for this nexus and matching client.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="nexusFactory">Factory used to instance nexuses for the server on each client connection. Useful to pass parameters to the nexus.</param>
    /// <returns>NexusServer for handling incoming connections.</returns>
    public static global::NexNet.NexusServer<global::NexNetSample.Asp.Server.ServerNexus, global::NexNetSample.Asp.Server.ServerNexus.ClientProxy> CreateServer(global::NexNet.Transports.ServerConfig config, global::System.Func<global::NexNetSample.Asp.Server.ServerNexus> nexusFactory)
    {
        return new global::NexNet.NexusServer<global::NexNetSample.Asp.Server.ServerNexus, global::NexNetSample.Asp.Server.ServerNexus.ClientProxy>(config, nexusFactory);
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
                    // void ServerVoid()
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerVoid();");
                    ServerVoid();
                    break;
                }
                case 1:
                {
                    // void ServerVoidWithParam(int id)
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerVoidWithParam(id = {arguments.Item1});");
                    ServerVoidWithParam(arguments.Item1);
                    break;
                }
                case 2:
                {
                    // ValueTask ServerTask()
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerTask();");
                    await ServerTask().ConfigureAwait(false);
                    break;
                }
                case 3:
                {
                    // ValueTask ServerTaskWithParam(int data)
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerTaskWithParam(data = {arguments.Item1});");
                    await ServerTaskWithParam(arguments.Item1).ConfigureAwait(false);
                    break;
                }
                case 4:
                {
                    // ValueTask<ValueTask<int>> ServerTaskValue()
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerTaskValue();");
                    var result = await ServerTaskValue().ConfigureAwait(false);
                    if (returnBuffer != null)
                        global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                    break;
                }
                case 5:
                {
                    // ValueTask<ValueTask<int>> ServerTaskValueWithParam(int data)
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerTaskValueWithParam(data = {arguments.Item1});");
                    var result = await ServerTaskValueWithParam(arguments.Item1).ConfigureAwait(false);
                    if (returnBuffer != null)
                        global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                    break;
                }
                case 6:
                {
                    // ValueTask ServerTaskWithCancellation(CancellationToken cancellationToken)
                    cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerTaskWithCancellation(cancellationToken = ct);");
                    await ServerTaskWithCancellation(cts.Token).ConfigureAwait(false);
                    break;
                }
                case 7:
                {
                    // ValueTask ServerTaskWithValueAndCancellation(int value, CancellationToken cancellationToken)
                    cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerTaskWithValueAndCancellation(value = {arguments.Item1}, cancellationToken = ct);");
                    await ServerTaskWithValueAndCancellation(arguments.Item1, cts.Token).ConfigureAwait(false);
                    break;
                }
                case 8:
                {
                    // ValueTask<ValueTask<int>> ServerTaskValueWithCancellation(CancellationToken cancellationToken)
                    cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerTaskValueWithCancellation(cancellationToken = ct);");
                    var result = await ServerTaskValueWithCancellation(cts.Token).ConfigureAwait(false);
                    if (returnBuffer != null)
                        global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                    break;
                }
                case 9:
                {
                    // ValueTask<ValueTask<int>> ServerTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken)
                    cts = methodInvoker.RegisterCancellationToken(message.InvocationId);
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Int32>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerTaskValueWithValueAndCancellation(value = {arguments.Item1}, cancellationToken = ct);");
                    var result = await ServerTaskValueWithValueAndCancellation(arguments.Item1, cts.Token).ConfigureAwait(false);
                    if (returnBuffer != null)
                        global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
                    break;
                }
                case 11:
                {
                    // ValueTask ServerData(byte[] data)
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Byte[]>>();
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                        ? global::NexNet.Logging.NexusLogLevel.Information
                        : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerData(data = {arguments.Item1});");
                    await ServerData(arguments.Item1).ConfigureAwait(false);
                    break;
                }
                case 100:
                {
                    // ValueTask ServerTaskValueWithDuplexPipe(INexusDuplexPipe pipe)
                    var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Byte>>();
                    duplexPipe = await methodInvoker.RegisterDuplexPipe(arguments.Item1).ConfigureAwait(false);
                    this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 
                            ? global::NexNet.Logging.NexusLogLevel.Information
                            : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $"Invoking Method: ServerTaskValueWithDuplexPipe(pipe = {arguments.Item1});");

                    await Unsafe.As<ICollectionStore>(this).StartCollection<int>(100, duplexPipe);
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
    static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => -681352088; }

    /// <summary>
    /// Proxy invocation implementation for the matching nexus.
    /// </summary>
    public class ClientProxy : global::NexNet.Invocation.ProxyInvocationBase, global::NexNetSample.Asp.Shared.IClientNexus, global::NexNet.Invocation.IInvocationMethodHash
    {
         public void ClientVoid()
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientVoid();");
             _ = __proxyInvoker.ProxyInvokeMethodCore(0, null, global::NexNet.Messages.InvocationFlags.None);
         }
         public void ClientVoidWithParam(global::System.Int32 id)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(id);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientVoidWithParam(id = {__proxyInvocationArguments.Item1});");
             _ = __proxyInvoker.ProxyInvokeMethodCore(1, __proxyInvocationArguments, global::NexNet.Messages.InvocationFlags.None);
         }
         public global::System.Threading.Tasks.ValueTask ClientTask()
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientTask();");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore(2, null, null);
         }
         public global::System.Threading.Tasks.ValueTask ClientTaskWithParam(global::System.Int32 data)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(data);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientTaskWithParam(data = {__proxyInvocationArguments.Item1});");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore(3, __proxyInvocationArguments, null);
         }
         public global::System.Threading.Tasks.ValueTask<global::System.Int32> ClientTaskValue()
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientTaskValue();");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore<global::System.Int32>(4, null, null);
         }
         public global::System.Threading.Tasks.ValueTask<global::System.Int32> ClientTaskValueWithParam(global::System.Int32 data)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(data);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientTaskValueWithParam(data = {__proxyInvocationArguments.Item1});");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore<global::System.Int32>(5, __proxyInvocationArguments, null);
         }
         public global::System.Threading.Tasks.ValueTask ClientTaskWithCancellation(global::System.Threading.CancellationToken cancellationToken)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientTaskWithCancellation(cancellationToken);");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore(6, null, cancellationToken);
         }
         public global::System.Threading.Tasks.ValueTask ClientTaskWithValueAndCancellation(global::System.Int32 value, global::System.Threading.CancellationToken cancellationToken)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(value);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientTaskWithValueAndCancellation(value = {__proxyInvocationArguments.Item1}, cancellationToken);");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore(7, __proxyInvocationArguments, cancellationToken);
         }
         public global::System.Threading.Tasks.ValueTask<global::System.Int32> ClientTaskValueWithCancellation(global::System.Threading.CancellationToken cancellationToken)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientTaskValueWithCancellation(cancellationToken);");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore<global::System.Int32>(8, null, cancellationToken);
         }
         public global::System.Threading.Tasks.ValueTask<global::System.Int32> ClientTaskValueWithValueAndCancellation(global::System.Int32 value, global::System.Threading.CancellationToken cancellationToken)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Int32>(value);
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientTaskValueWithValueAndCancellation(value = {__proxyInvocationArguments.Item1}, cancellationToken);");
             return __proxyInvoker.ProxyInvokeAndWaitForResultCore<global::System.Int32>(9, __proxyInvocationArguments, cancellationToken);
         }
         public global::System.Threading.Tasks.ValueTask ClientTaskValueWithDuplexPipe(global::NexNet.Pipes.INexusDuplexPipe pipe)
         {
             var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);
             var __proxyInvocationArguments = new global::System.ValueTuple<global::System.Byte>(__proxyInvoker.ProxyGetDuplexPipeInitialId(pipe));
             __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $"Proxy Invoking Method: ClientTaskValueWithDuplexPipe(pipe = {__proxyInvocationArguments.Item1});");
             return __proxyInvoker.ProxyInvokeMethodCore(10, __proxyInvocationArguments, global::NexNet.Messages.InvocationFlags.DuplexPipe);
         }

        /// <summary>
        /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 1326271065; }
    }
}
