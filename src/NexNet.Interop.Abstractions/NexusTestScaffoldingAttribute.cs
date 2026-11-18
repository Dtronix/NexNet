using System;

namespace NexNet.Interop;

/// <summary>
/// Asks the NexNet generator to emit the C# test scaffolding of a nexus pair into this assembly: the remote proxy
/// <c>Remote_&lt;IServerNexus&gt;</c> (implements <typeparamref name="TServerNexus"/> by forwarding every call to an
/// <see cref="IRemoteInvoker"/>), the script facade <c>&lt;ClientNexus&gt;Script</c> (one <see cref="ScriptSlot{TArgs, TResult}"/>
/// per method of <typeparamref name="TClientNexus"/>) and their typed-JSON codecs.
/// </summary>
/// <remarks>
/// Test-only. Each server and client interface is emitted once per assembly, however many pairs name it. Problems are
/// reported as NEXNET049.
/// </remarks>
/// <example>
/// <code>
/// [assembly: NexusTestScaffolding&lt;IServerNexus, IClientNexus&gt;]
/// </code>
/// </example>
/// <typeparam name="TServerNexus">The server nexus interface the client under test invokes.</typeparam>
/// <typeparam name="TClientNexus">The client nexus interface the server invokes on the client under test.</typeparam>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
// ReSharper disable twice UnusedTypeParameter
public sealed class NexusTestScaffoldingAttribute<TServerNexus, TClientNexus> : Attribute
    where TServerNexus : class
    where TClientNexus : class
{
}
