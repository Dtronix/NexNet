namespace NexNet;

/// <summary>
/// Identity interface for the authentication process.
/// </summary>
public interface IIdentity
{
    /// <summary>
    /// Name used for referencing this connection.
    /// </summary>
    string? DisplayName { get; }
}

/// <summary>
/// A simple default identity implementation
/// </summary>
public class DefaultIdentity : IIdentity
{
    /// <summary>
    /// Name used for referencing this connection.
    /// </summary>
    public string? DisplayName { get; set; }
}
