namespace NexNet.InteropServer.Probe;

/// <summary>
/// Server side of the probe nexus: a minimal nexus for the self-test and the console entry point, until the
/// interop test nexus (H02) is used.
/// </summary>
public partial interface IInteropProbeServerNexus
{
    /// <summary>Returns <paramref name="value"/>.</summary>
    ValueTask<string> Echo(string value);

    /// <summary>
    /// Describes the session's <c>Context.Endpoint</c> as <c>name|transport|isSecure|path</c> (<c>isSecure</c> is
    /// <c>true</c> or <c>false</c>, <c>path</c> empty when null); empty when the session has no endpoint.
    /// </summary>
    ValueTask<string> GetEndpoint();

    /// <summary>Returns the display name of the session's identity; empty when it has none.</summary>
    ValueTask<string> GetIdentity();
}

/// <summary>
/// Client side of the probe nexus. It has no methods.
/// </summary>
public partial interface IInteropProbeClientNexus
{
}

/// <summary>
/// Server nexus of the probe.
/// </summary>
[Nexus<IInteropProbeServerNexus, IInteropProbeClientNexus>(NexusType = NexusType.Server)]
public partial class InteropProbeServerNexus : IInteropTokenValidatingNexus
{
    /// <inheritdoc />
    public InteropTokenValidator? TokenValidator { get; set; }

    /// <summary>
    /// Formats an endpoint the way <see cref="IInteropProbeServerNexus.GetEndpoint"/> returns it.
    /// </summary>
    public static string Describe(NexusEndpointInfo? endpoint)
        => endpoint == null
            ? ""
            : $"{endpoint.Name}|{endpoint.Transport}|{(endpoint.IsSecure ? "true" : "false")}|{endpoint.Path}";

    /// <inheritdoc />
    protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? authenticationToken)
    {
        return TokenValidator == null
            ? ValueTask.FromResult<IIdentity?>(new DefaultIdentity())
            : TokenValidator(authenticationToken, Context.Endpoint);
    }

    /// <inheritdoc />
    public ValueTask<string> Echo(string value) => ValueTask.FromResult(value);

    /// <inheritdoc />
    public ValueTask<string> GetEndpoint() => ValueTask.FromResult(Describe(Context.Endpoint));

    /// <inheritdoc />
    public ValueTask<string> GetIdentity() => ValueTask.FromResult(Context.Identity?.DisplayName ?? "");
}

/// <summary>
/// Client nexus of the probe.
/// </summary>
[Nexus<IInteropProbeClientNexus, IInteropProbeServerNexus>(NexusType = NexusType.Client)]
public partial class InteropProbeClientNexus
{
}
