using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using NexNet.Generator.Models;

namespace NexNet.Generator.Emission;

/// <summary>
/// Emits generated code for a Nexus class.
/// Works entirely with cached data records - no semantic model access.
/// </summary>
internal static class NexusEmitter
{
    /// <summary>
    /// Emits the complete generated code for a Nexus.
    /// </summary>
    public static string Emit(NexusGenerationData data, LanguageVersion langVersion)
    {
        var sb = new StringBuilder();
        EmitNexus(sb, data);
        return sb.ToString();
    }

    private static string EmitServerClientName(NexusAttributeData attr) =>
        attr.IsServer ? "Server" : "Client";

    private static void EmitNexus(StringBuilder sb, NexusGenerationData data)
    {
        var collections = data.NexusAttribute.IsServer
            ? data.NexusInterface.AllCollections
            : data.ProxyInterface.AllCollections;

        sb.AppendLine($$"""
                        namespace {{data.Namespace}}
                        {
                            /// <summary>
                            /// Nexus used for handling all {{EmitServerClientName(data.NexusAttribute)}} communications.
                            /// </summary>
                            /// <remarks>
                        """);

        if (data.NexusAttribute.IsServer)
        {
            foreach (var nexusInterface in data.NexusInterface.Interfaces.Concat([data.NexusInterface]))
            {
                sb.Append("    /// ").Append(nexusInterface.TypeName).Append(" [NexusVersion(Version=\"")
                    .Append(nexusInterface.VersionAttribute?.Version ?? "")
                    .Append("\", HashLock=")
                    .Append(nexusInterface.NexusHash)
                    .AppendLine(")]");
            }
        }

        sb.AppendLine($$"""
                            /// </remarks>
                            partial class {{data.TypeName}} :
                                global::NexNet.Invocation.{{EmitServerClientName(data.NexusAttribute)}}NexusBase<{{data.Namespace}}.{{data.TypeName}}.{{data.ProxyInterface.ProxyImplName}}>,
                                {{data.NexusInterface.Namespace}}.{{data.NexusInterface.TypeName}},
                                global::NexNet.Invocation.IInvocationMethodHash,
                                global::NexNet.Collections.ICollectionConfigurer
                            {

                        """);

        if (data.NexusAttribute.IsServer)
        {
            foreach (var collection in data.NexusInterface.Collections)
            {
                CollectionEmitter.EmitNexusAccessor(sb, collection);
                sb.AppendLine();
            }

            sb.AppendLine($$"""
                                    /// <summary>
                                    /// Creates an instance of the server for this nexus and matching client.
                                    /// </summary>
                                    /// <param name="config">Configurations for this instance.</param>
                                    /// <param name="nexusFactory">Factory used to instance nexuses for the server on each client connection. Useful to pass parameters to the nexus.</param>
                                    /// <returns>NexusServer for handling incoming connections.</returns>
                                    public static global::NexNet.NexusServer<{{data.Namespace}}.{{data.TypeName}}, {{data.Namespace}}.{{data.TypeName}}.{{data.ProxyInterface.ProxyImplName}}> CreateServer(
                                        global::NexNet.Transports.ServerConfig config, global::System.Func<{{data.Namespace}}.{{data.TypeName}}> nexusFactory,
                                        global::System.Action<{{data.Namespace}}.{{data.TypeName}}>? collectionConfigurer = null)
                                    {
                                        return new global::NexNet.NexusServer<{{data.Namespace}}.{{data.TypeName}}, {{data.Namespace}}.{{data.TypeName}}.{{data.ProxyInterface.ProxyImplName}}>(config, nexusFactory, collectionConfigurer);
                                    }
                            """);
        }
        else
        {
            sb.AppendLine($$"""
                                    /// <summary>
                                    /// Creates an instance of the client for this nexus and matching server.
                                    /// </summary>
                                    /// <param name="config">Configurations for this instance.</param>
                                    /// <param name="nexus">Nexus used for this client while communicating with the server. Useful to pass parameters to the nexus.</param>
                                    /// <param name="collectionConfigurer">Configures any collections upon starting the server.</param>
                                    /// <returns>NexusClient for connecting to the matched NexusServer.</returns>
                                    public static global::NexNet.NexusClient<{{data.Namespace}}.{{data.TypeName}}, {{data.Namespace}}.{{data.TypeName}}.{{data.ProxyInterface.ProxyImplName}}> CreateClient(global::NexNet.Transports.ClientConfig config, {{data.TypeName}} nexus)
                                    {
                                        return new global::NexNet.NexusClient<{{data.Namespace}}.{{data.TypeName}}, {{data.Namespace}}.{{data.TypeName}}.{{data.ProxyInterface.ProxyImplName}}>(config, nexus);
                                    }
                            """);
        }

        sb.AppendLine($$"""

                                protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
                                {
                                    global::System.Threading.CancellationTokenSource? cts = null;
                                    global::NexNet.Pipes.INexusDuplexPipe? duplexPipe = null;
                                    var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker>(this);
                                    try
                                    {
                        """);

        if (data.NexusInterface.AllMethods.Length > 0 || data.NexusInterface.AllCollections.Length > 0)
        {
            sb.AppendLine($$"""
                                            switch (message.MethodId)
                                            {
                            """);

            foreach (var method in data.NexusInterface.AllMethods)
            {
                sb.Append($$"""
                                                case {{method.Id}}:
                                                {
                                                    // {{MethodEmitter.ToStringRepresentation(method)}}

                            """);
                MethodEmitter.EmitNexusInvocation(sb, method, data.ProxyInterface);
                sb.AppendLine("""
                                                      break;
                                                  }
                              """);
            }

            if (data.NexusInterface.Collections.Length > 0)
            {
                sb.AppendLine("                    // Collection invocations:");
            }

            for (int i = 0; i < data.NexusInterface.Collections.Length; i++)
            {
                var collection = data.NexusInterface.Collections[i];
                sb.Append($$"""
                                                case {{collection.Id}}:
                                                {
                                                    // {{CollectionEmitter.ToStringRepresentation(collection)}}
                            """);
                CollectionEmitter.EmitNexusInvocation(sb, collection);
                sb.AppendLine("""
                                                      break;
                                                  }
                              """);
            }

            sb.AppendLine($$"""
                                            }
                            """);
        }
        else
        {
            sb.AppendLine("                // No methods.");
        }

        sb.AppendLine($$"""
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

                        """);

        sb.AppendLine($$"""
                                /// <summary>
                                /// Configures the nexus collections, if there are any.
                                /// </summary>
                                /// <param name="manager">Manager for configuring collections</param>
                                static void global::NexNet.Collections.ICollectionConfigurer.ConfigureCollections(global::NexNet.Invocation.IConfigureCollectionManager manager)
                                {
                        """);

        if (collections.Length > 0)
        {
            foreach (var collection in collections)
            {
                CollectionEmitter.EmitCollectionConfigure(sb, collection);
            }
        }
        else
        {
            sb.AppendLine("            // No collections configured.");
        }

        sb.AppendLine($$"""
                                    manager.CompleteConfigure();
                                }

                        """);

        sb.Append($$"""
                            /// <summary>
                            /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
                            /// </summary>
                            static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => {{data.NexusInterface.NexusHash}}; }

                            /// <summary>
                            /// Hash table for all the versions that this nexus implements.
                            /// </summary>
                            static global::System.Collections.Frozen.FrozenDictionary<string, int> global::NexNet.Invocation.IInvocationMethodHash.VersionHashTable { get; } =
                    """);

        if (data.NexusAttribute.IsServer && data.NexusInterface.Versions.Length > 0)
        {
            sb.AppendLine(
                "global::System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.KeyValuePair<string, int>[] {");
            foreach (var versionInterface in data.NexusInterface.Versions)
            {
                sb.Append("            new(\"").Append(versionInterface.VersionAttribute?.Version ?? "")
                    .Append("\", ")
                    .Append(versionInterface.NexusHash).AppendLine("),");
            }

            sb.AppendLine("        });");
        }
        else
        {
            sb.AppendLine("global::System.Collections.Frozen.FrozenDictionary<string, int>.Empty;");
        }

        sb.AppendLine();

        sb.Append($$"""
                            /// <summary>
                            /// Version + Method hashes.  Only implemented on the server nexus.
                            /// </summary>
                            static global::System.Collections.Frozen.FrozenSet<long> global::NexNet.Invocation.IInvocationMethodHash.VersionMethodHashSet { get; } =
                    """);

        sb.AppendLine("global::System.Collections.Frozen.FrozenSet.ToFrozenSet(new long[] {");
        if (data.NexusAttribute.IsServer && data.NexusInterface.Versions.Length > 0)
        {
            foreach (var versionInterface in data.NexusInterface.Versions)
            {
                var versionHash = versionInterface.NexusHash;

                foreach (var method in versionInterface.AllMethods)
                {
                    sb.Append("            Util.VMHash(").Append(versionHash).Append(", ").Append(method.Id)
                        .AppendLine("),");
                }

                foreach (var collection in versionInterface.AllCollections)
                {
                    sb.Append("            Util.VMHash(").Append(versionHash).Append(", ").Append(collection.Id)
                        .AppendLine("),");
                }
            }
        }
        else
        {
            var versionHash = data.NexusInterface.NexusHash;
            foreach (var method in data.NexusInterface.AllMethods)
            {
                sb.Append("            Util.VMHash(").Append(versionHash).Append(", ").Append(method.Id)
                    .AppendLine("),");
            }

            foreach (var collection in data.NexusInterface.AllCollections)
            {
                sb.Append("            Util.VMHash(").Append(versionHash).Append(", ").Append(collection.Id)
                    .AppendLine("),");
            }
        }

        sb.AppendLine("        });");

        // Emit proxy implementation - only on client
        InvocationInterfaceEmitter.EmitProxyImpl(
            sb,
            data.ProxyInterface,
            data.NexusAttribute,
            data.NexusAttribute.IsServer ? null : data.ProxyInterface);

        sb.AppendLine($$"""
                            }

                            static file class Util
                            {

                                [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                                public static long VMHash(int version, ushort methodId) => ((long)version << 16) | methodId;

                                public static void NexusLog(this global::NexNet.Logging.INexusLogger logger, string message)
                                {
                                 logger.Log((logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0
                                     ? global::NexNet.Logging.NexusLogLevel.Information
                                     : global::NexNet.Logging.NexusLogLevel.Debug, null, null, message);
                                }

                                public static void ProxyLog(this global::NexNet.Logging.INexusLogger logger, string message)
                                {
                                 logger.Log((logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0
                                     ? global::NexNet.Logging.NexusLogLevel.Information
                                     : global::NexNet.Logging.NexusLogLevel.Debug, null, null, message);
                                }
                            }
                        }


                        """);
    }
}
