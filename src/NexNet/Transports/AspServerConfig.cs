namespace NexNet.Transports;

/// <summary>
/// Base configuration file for NexNet servers running through ASP.
/// </summary>
public abstract class AspServerConfig : ServerConfig
{
    /// <summary>
    /// Path that the NexNet server binds to on the host.
    /// </summary>
    public string Path { get; set; } = null!;

    /// <summary>
    /// Enables authentication of the ASP server on the specified path.
    /// </summary>
    public bool AspEnableAuthentication { get; set; } = false;

    /// <summary>
    /// ASP Authentication scheme to apply.  Null to use the default authentication scheme.
    /// </summary>
    public string? AspAuthenticationScheme { get; set; } = null;

    /// <summary>
    /// Sets the server mode.
    /// </summary>
    protected AspServerConfig()
        : base(ServerConnectionMode.Receiver)
    {
        
    }
}
