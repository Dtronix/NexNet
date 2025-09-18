using System.Text;

namespace NexNet.Generator.MetaGenerator;

partial class NexusMeta
{
    private string EmitServerClientName() => NexusAttribute.IsServer ? "Server" : "Client";

    public void EmitNexus(StringBuilder sb)
    {
        var collections = NexusAttribute.IsServer ? NexusInterface.AllCollections : ProxyInterface.AllCollections;
        sb.AppendLine($$"""
                        namespace {{Symbol.ContainingNamespace}} 
                        {
                            /// <summary>
                            /// Nexus used for handling all {{EmitServerClientName()}} communications.
                            /// </summary>
                            /// <remarks>
                        """);
        if (NexusAttribute.IsServer)
        {
            foreach (var nexusInterface in this.NexusInterface.Interfaces.Concat([this.NexusInterface]))
            {
                sb.Append("    /// ").Append(nexusInterface.TypeName).Append(" [NexusVersion(Version=\"")
                    .Append(nexusInterface.VersionAttribute.Version).Append("\", HashLock=")
                    .Append(nexusInterface.GetNexusHash())
                    .AppendLine(")]");
            }

            ;
        }

        sb.AppendLine($$"""
                            /// </remarks>
                            partial class {{TypeName}} : 
                                global::NexNet.Invocation.{{EmitServerClientName()}}NexusBase<{{this.Namespace}}.{{this.TypeName}}.{{this.ProxyInterface.ProxyImplName}}>, 
                                {{this.NexusInterface.Namespace}}.{{this.NexusInterface.TypeName}}, 
                                global::NexNet.Invocation.IInvocationMethodHash,
                                global::NexNet.Collections.ICollectionConfigurer
                            {
                            
                        """);
        if (NexusAttribute.IsServer)
        {
            foreach (var collection in NexusInterface.Collections)
            {
                collection.EmitNexusAccessor(sb);
                sb.AppendLine();
            }

            sb.AppendLine($$"""
                                    /// <summary>
                                    /// Creates an instance of the server for this nexus and matching client.
                                    /// </summary>
                                    /// <param name="config">Configurations for this instance.</param>
                                    /// <param name="nexusFactory">Factory used to instance nexuses for the server on each client connection. Useful to pass parameters to the nexus.</param>
                                    /// <returns>NexusServer for handling incoming connections.</returns>
                                    public static global::NexNet.NexusServer<{{this.Namespace}}.{{TypeName}}, {{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}> CreateServer(
                                        global::NexNet.Transports.ServerConfig config, global::System.Func<{{this.Namespace}}.{{TypeName}}> nexusFactory,
                                        global::System.Action<{{this.Namespace}}.{{TypeName}}>? collectionConfigurer = null)
                                    {
                                        return new global::NexNet.NexusServer<{{this.Namespace}}.{{TypeName}}, {{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}>(config, nexusFactory, collectionConfigurer);
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
                                    public static global::NexNet.NexusClient<{{this.Namespace}}.{{TypeName}}, {{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}> CreateClient(global::NexNet.Transports.ClientConfig config, {{TypeName}} nexus)
                                    {
                                        return new global::NexNet.NexusClient<{{this.Namespace}}.{{TypeName}}, {{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}>(config, nexus);
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
        if (NexusInterface.AllMethods!.Length > 0 || NexusInterface.AllCollections!.Length > 0)
        {
            sb.AppendLine($$"""
                                            switch (message.MethodId)
                                            {
                            """);


            foreach (var method in NexusInterface.AllMethods)
            {
                sb.Append($$"""
                                                case {{method.Id}}:
                                                {
                                                    // {{method}}

                            """);
                method.EmitNexusInvocation(sb, this.ProxyInterface, this);
                sb.AppendLine("""
                                                      break;
                                                  }
                              """);
            }

            if (NexusInterface.Collections.Length > 0)
            {
                sb.AppendLine("                    // Collection invocations:");
            }

            for (int i = 0; i < NexusInterface.Collections.Length; i++)
            {
                sb.Append($$"""
                                                case {{NexusInterface.Collections[i].Id}}:
                                                {
                                                    // {{NexusInterface.Collections[i]}}
                            """);
                NexusInterface.Collections[i].EmitNexusInvocation(sb);
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
        if (collections!.Length > 0)
        {
            foreach (var collection in collections)
            {
                collection.EmitCollectionConfigure(sb);
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
                            static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => {{this.NexusInterface.GetNexusHash()}}; }
                            
                            /// <summary>
                            /// Hash table for all the versions that this nexus implements.
                            /// </summary>
                            static global::System.Collections.Frozen.FrozenDictionary<string, int> global::NexNet.Invocation.IInvocationMethodHash.VersionHashTable { get; } = 
                    """);

        if (NexusAttribute.IsServer && this.NexusInterface.Versions.Length > 0)
        {
            sb.AppendLine(
                "global::System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.KeyValuePair<string, int>[] {");
            foreach (var versionInterface in this.NexusInterface.Versions)
            {
                sb.Append("            new(\"").Append(versionInterface.VersionAttribute.Version).Append("\", ")
                    .Append(versionInterface.GetNexusHash()).AppendLine("),");
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
        if (NexusAttribute.IsServer && this.NexusInterface.Versions.Length > 0)
        {
            foreach (var versionInterface in this.NexusInterface.Versions)
            {
                var versionHash = versionInterface.GetNexusHash();

                foreach (var method in versionInterface.AllMethods!)
                {
                    sb.Append("            Util.VMHash(").Append(versionHash).Append(", ").Append(method.Id).AppendLine("),");
                }
                
                foreach (var collection in versionInterface.AllCollections!)
                {
                    sb.Append("            Util.VMHash(").Append(versionHash).Append(", ").Append(collection.Id).AppendLine("),");
                }
            }
        }
        else
        {
            var versionHash = this.NexusInterface.GetNexusHash();
            foreach (var method in NexusInterface.AllMethods!)
            {
                sb.Append("            Util.VMHash(").Append(versionHash).Append(", ").Append(method.Id).AppendLine("),");
            }
                
            foreach (var collection in NexusInterface.AllCollections!)
            {
                sb.Append("            Util.VMHash(").Append(versionHash).Append(", ").Append(collection.Id).AppendLine("),");
            }
        }
        
        sb.AppendLine("        });");


        // We don't want to emit the proxy invocation on the server, only on the client.
        ProxyInterface.EmitProxyImpl(sb, NexusAttribute.IsServer ? null : ProxyInterface);

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

    public override string ToString()
    {
        return this.TypeName;
    }
}
