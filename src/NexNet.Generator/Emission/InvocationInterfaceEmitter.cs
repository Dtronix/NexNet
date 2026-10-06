using System.Text;
using NexNet.Generator.Models;

namespace NexNet.Generator.Emission;

/// <summary>
/// Emits proxy implementation code for invocation interfaces.
/// Works entirely with cached data records - no semantic model access.
/// </summary>
internal static class InvocationInterfaceEmitter
{
    /// <summary>
    /// Emits the proxy implementation class.
    /// </summary>
    public static void EmitProxyImpl(
        StringBuilder sb,
        InvocationInterfaceData interfaceData,
        NexusAttributeData nexusAttribute,
        InvocationInterfaceData? proxyInterface)
    {
        sb.AppendLine($$"""

        /// <summary>
        /// Proxy invocation implementation for the matching nexus.
        /// </summary>
        public class {{interfaceData.ProxyImplName}} : global::NexNet.Invocation.ProxyInvocationBase, {{interfaceData.Namespace}}.{{interfaceData.TypeName}}, global::NexNet.Invocation.IInvocationMethodHash
        {
""");

        if (proxyInterface != null)
        {
            foreach (var collection in proxyInterface.Collections)
            {
                CollectionEmitter.EmitProxyAccessor(sb, collection);
                sb.AppendLine();
            }
        }

        foreach (var method in interfaceData.AllMethods)
        {
            MethodEmitter.EmitProxyMethodInvocation(sb, method);
        }

        sb.Append($$"""

            /// <summary>
            /// Hash for methods on this proxy or nexus.  Used to perform a simple client and server match check.
            /// </summary>
            static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => {{interfaceData.NexusHash}}; }

            /// <summary>
            /// Hash table for all the versions that this proxy can invoke.  Empty if the proxy is not versioning.
            /// </summary>
            static global::System.Collections.Frozen.FrozenDictionary<string, int> global::NexNet.Invocation.IInvocationMethodHash.VersionHashTable { get; } =
""");

        if (nexusAttribute.IsServer)
        {
            // Server proxy doesn't need version info
            sb.AppendLine("global::System.Collections.Frozen.FrozenDictionary<string, int>.Empty;");
        }
        else
        {
            // On the client proxy, we only emit the version ID specified on the nexus interface
            // If we have a versioning server.
            var lastVersion = interfaceData.Versions.Length > 0
                ? interfaceData.Versions[interfaceData.Versions.Length - 1]
                : null;

            if (lastVersion == null)
            {
                sb.AppendLine("global::System.Collections.Frozen.FrozenDictionary<string, int>.Empty;");
            }
            else
            {
                sb.AppendLine(
                    "global::System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.KeyValuePair<string, int>[] {");
                sb.Append("                new(\"").Append(lastVersion.VersionAttribute?.Version ?? "")
                    .Append("\", ").Append(lastVersion.NexusHash).AppendLine("),");
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
}
