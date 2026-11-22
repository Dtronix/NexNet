using System;

namespace NexNet;

/// <summary>
/// Client languages the generator produces code for, in addition to C#. Set on a server nexus with
/// <see cref="NexusAttribute{TNexusInterface, TProxyInterface}.Targets"/> or on an assembly with
/// <see cref="NexusClientExportAttribute{TServerNexus, TClientNexus}"/>.
/// </summary>
/// <remarks>
/// Values are bit flags and are stored by value in generated artifacts, so existing values never change.
/// </remarks>
[Flags]
public enum NexusTargets
{
    /// <summary>
    /// No target language: only the C# client is generated (the default).
    /// </summary>
    None = 0,

    /// <summary>
    /// Java client (<c>com.dtronix:nexnet-client</c>).
    /// </summary>
    Java = 1,

    /// <summary>
    /// JavaScript client (<c>@dtronix/nexnet</c>), for browsers and Node.
    /// </summary>
    JavaScript = 2,

    // Reserved: Rust = 4, Cpp = 8.
}
