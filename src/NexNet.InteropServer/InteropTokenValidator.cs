namespace NexNet.InteropServer;

/// <summary>
/// Validates the NexNet greeting token (<c>ClientConfig.Authenticate</c>) of a connecting client.
/// </summary>
/// <param name="token">The token the client sent; null when it sent none.</param>
/// <param name="endpoint">The endpoint the client connected to.</param>
/// <returns>The identity of an accepted client; null refuses the connection.</returns>
public delegate ValueTask<IIdentity?> InteropTokenValidator(ReadOnlyMemory<byte>? token, NexusEndpointInfo? endpoint);

/// <summary>
/// A server nexus that delegates <c>OnAuthenticate</c> to the interop server's
/// <see cref="InteropServerOptions.TokenValidator"/>. The interop server sets <see cref="TokenValidator"/> on every
/// nexus it creates that implements this interface.
/// </summary>
public interface IInteropTokenValidatingNexus
{
    /// <summary>
    /// The validator to call from <c>OnAuthenticate</c>; null when the server does not authenticate.
    /// </summary>
    InteropTokenValidator? TokenValidator { get; set; }
}
