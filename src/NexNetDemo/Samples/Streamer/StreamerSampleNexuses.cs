using NexNet;

namespace NexNetDemo.Samples.Streamer;

interface IStreamerSampleClientNexus
{
}

interface IStreamerSampleServerNexus
{
    ValueTask BroadcastMessage(INexusDuplexPipe message);;
}


//[Nexus<IStreamerSampleClientNexus, IStreamerSampleServerNexus>(NexusType = NexusType.Client)]
partial class StreamerSampleClientNexus
{
    public ValueTask SendMessage(string message)
    {
        Console.WriteLine(message);
        return default;
    }

}

//[Nexus<IStreamerSampleServerNexus, IStreamerSampleClientNexus>(NexusType = NexusType.Server)]
partial class StreamerSampleServerNexus
{
    public ValueTask BroadcastMessage(INexusDuplexPipe message)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Nexus used for handling all Client communications.
/// </summary>
partial class StreamerSampleClientNexus : global::NexNet.Invocation.ClientNexusBase<global::NexNetDemo.Samples.Streamer.StreamerSampleClientNexus.ServerProxy>, global::NexNetDemo.Samples.Streamer.IStreamerSampleClientNexus, global::NexNet.Invocation.IInvocationMethodHash
{
    /// <summary>
    /// Creates an instance of the client for this nexus and matching server.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="nexus">Nexus used for this client while communicating with the server. Useful to pass parameters to the nexus.</param>
    /// <returns>NexusClient for connecting to the matched NexusServer.</returns>
    public static global::NexNet.NexusClient<global::NexNetDemo.Samples.Streamer.StreamerSampleClientNexus, global::NexNetDemo.Samples.Streamer.StreamerSampleClientNexus.ServerProxy> CreateClient(global::NexNet.Transports.ClientConfig config, StreamerSampleClientNexus nexus)
    {
        return new global::NexNet.NexusClient<global::NexNetDemo.Samples.Streamer.StreamerSampleClientNexus, global::NexNetDemo.Samples.Streamer.StreamerSampleClientNexus.ServerProxy>(config, nexus);
    }

    protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
    {
        global::System.Threading.CancellationTokenSource? cts = null;
        global::NexNet.INexusDuplexPipe? duplexPipe = null;
        var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker>(this);
        try
        {
            // No methods.
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
    static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 0; }

    /// <summary>
    /// Proxy invocation implementation for the matching nexus.
    /// </summary>
    public class ServerProxy : global::NexNet.Invocation.ProxyInvocationBase, global::NexNetDemo.Samples.Streamer.IStreamerSampleServerNexus, global::NexNet.Invocation.IInvocationMethodHash
    {
        public global::System.Threading.Tasks.ValueTask BroadcastMessage(global::NexNet.INexusDuplexPipe message)
        {
            var arguments = new global::System.ValueTuple<global::System.Byte>(__ProxyGetDuplexPipeInitialId(message));
            return __ProxyInvokeMethodCore(0, arguments, global::NexNet.Messages.InvocationFlags.DuplexPipe);
        }

        /// <summary>
        /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 1733631527; }
    }
}

/// <summary>
/// Nexus used for handling all Server communications.
/// </summary>
partial class StreamerSampleServerNexus : global::NexNet.Invocation.ServerNexusBase<global::NexNetDemo.Samples.Streamer.StreamerSampleServerNexus.ClientProxy>, global::NexNetDemo.Samples.Streamer.IStreamerSampleServerNexus, global::NexNet.Invocation.IInvocationMethodHash
{
    /// <summary>
    /// Creates an instance of the server for this nexus and matching client.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="nexusFactory">Factory used to instance nexuses for the server on each client connection. Useful to pass parameters to the nexus.</param>
    /// <returns>NexusServer for handling incoming connections.</returns>
    public static global::NexNet.NexusServer<global::NexNetDemo.Samples.Streamer.StreamerSampleServerNexus, global::NexNetDemo.Samples.Streamer.StreamerSampleServerNexus.ClientProxy> CreateServer(global::NexNet.Transports.ServerConfig config, global::System.Func<global::NexNetDemo.Samples.Streamer.StreamerSampleServerNexus> nexusFactory)
    {
        return new global::NexNet.NexusServer<global::NexNetDemo.Samples.Streamer.StreamerSampleServerNexus, global::NexNetDemo.Samples.Streamer.StreamerSampleServerNexus.ClientProxy>(config, nexusFactory);
    }

    protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
    {
        global::System.Threading.CancellationTokenSource? cts = null;
        global::NexNet.INexusDuplexPipe? duplexPipe = null;
        var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker>(this);
        try
        {
            switch (message.MethodId)
            {
                case 0:
                    {
                        // ValueTask BroadcastMessage(INexusDuplexPipe message)
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Byte>>();
                        duplexPipe = await methodInvoker.RegisterDuplexPipe(arguments.Item1);
                        await BroadcastMessage(duplexPipe);
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
    static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 1733631527; }

    /// <summary>
    /// Proxy invocation implementation for the matching nexus.
    /// </summary>
    public class ClientProxy : global::NexNet.Invocation.ProxyInvocationBase, global::NexNetDemo.Samples.Streamer.IStreamerSampleClientNexus, global::NexNet.Invocation.IInvocationMethodHash
    {

        /// <summary>
        /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => 0; }
    }
}
