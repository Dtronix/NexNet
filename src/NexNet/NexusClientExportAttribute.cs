using System;

namespace NexNet;

/// <summary>
/// Exports a client for a server nexus interface to other languages without a nexus class. Use it to target an older
/// server API version (for example <c>IServerNexusV1</c>) or an API whose server lives in another assembly.
/// </summary>
/// <remarks>
/// <para>
/// The generator builds the export from the two interfaces exactly as it would build a C# client nexus
/// <c>[Nexus&lt;TClientNexus, TServerNexus&gt;(NexusType = NexusType.Client)]</c>: same method ids, hashes and handshake.
/// Methods are not marked as authorized because no server class is involved.
/// </para>
/// <para>
/// An export with <see cref="NexusTargets.None"/> generates nothing.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [assembly: NexusClientExport&lt;IChatServerNexusV1, IChatClientNexus&gt;(Targets = NexusTargets.JavaScript, Name = "ChatV1")]
/// </code>
/// </example>
/// <typeparam name="TServerNexus">The server nexus interface the client invokes.</typeparam>
/// <typeparam name="TClientNexus">The client nexus interface the server invokes on the client.</typeparam>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
// ReSharper disable twice UnusedTypeParameter
public sealed class NexusClientExportAttribute<TServerNexus, TClientNexus> : Attribute
    where TServerNexus : class
    where TClientNexus : class
{
    /// <summary>
    /// Languages to generate the client for.
    /// </summary>
    public required NexusTargets Targets { get; init; }

    /// <summary>
    /// Name of the export, used to name the generated client types. Defaults to the server interface name without
    /// its leading <c>I</c> (<c>IChatServerNexusV1</c> gives <c>ChatServerNexusV1</c>).
    /// </summary>
    public string? Name { get; init; }
}
