using System.Text;

namespace NexNet.Generator.MetaGenerator;

partial class InvocationInterfaceMeta
{
    public void EmitProxyImpl(StringBuilder sb, InvocationInterfaceMeta? proxyInterface)
    {
        sb.AppendLine($$"""

        /// <summary>
        /// Proxy invocation implementation for the matching nexus.
        /// </summary>
        public class {{ProxyImplName}} : global::NexNet.Invocation.ProxyInvocationBase, {{this.Namespace}}.{{this.TypeName}}, global::NexNet.Invocation.IInvocationMethodHash
        {
""");
        if (proxyInterface != null)
        {
            foreach (var collection in proxyInterface.Collections)
            {
                collection.EmitProxyAccessor(sb);
                sb.AppendLine();
            }
        }

        foreach (var method in AllMethods!)
        {
            method.EmitProxyMethodInvocation(sb);
        }


        sb.Append($$"""

            /// <summary>
            /// Hash for methods on this proxy or nexus.  Used to perform a simple client and server match check.
            /// </summary>
            static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => {{GetNexusHash()}}; }
            
            /// <summary>
            /// Hash table for all the versions that this proxy can invoke.  Empty if the proxy is not versioning.
            /// </summary>
            static global::System.Collections.Frozen.FrozenDictionary<string, int> global::NexNet.Invocation.IInvocationMethodHash.VersionHashTable { get; } = 
""");

        if (NexusAttribute.IsServer)
        {
            // 
            sb.AppendLine("global::System.Collections.Frozen.FrozenDictionary<string, int>.Empty;");
        }
        else
        {
            // On the client proxy, we only emit the version ID specified on the nexus interface
            // If we have a versioning server.
            var lastVersion = Versions.LastOrDefault();

            if (lastVersion == null)
            {
                sb.AppendLine("global::System.Collections.Frozen.FrozenDictionary<string, int>.Empty;");
            }
            else
            {
                sb.AppendLine(
                "global::System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.KeyValuePair<string, int>[] {");
            sb.Append("                new(\"").Append(lastVersion.VersionAttribute.Version).Append("\", ").Append(lastVersion?.GetNexusHash() ?? GetNexusHash()).AppendLine("),");
                    sb.AppendLine("""
            });
""");
            }

        }
        
        sb.AppendLine($$"""

            /// <summary>
            /// Version + Method hashes.  Always empty on the proxy.
            /// </summary>
            static global::System.Collections.Frozen.FrozenSet<long> global::NexNet.Invocation.IInvocationMethodHash.VersionMethodHashSet { get; } = global::System.Collections.Frozen.FrozenSet<long>.Empty; 
""");
        
        sb.AppendLine("""
        }
""");
        
    }
    
    public override string ToString()
    {
        return this.TypeName;
    }
}
