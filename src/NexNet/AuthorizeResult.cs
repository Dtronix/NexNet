namespace NexNet;

/// <summary>
/// Result of an authorization check on a nexus method invocation.
/// </summary>
public enum AuthorizeResult
{
    /// <summary>
    /// Invocation is allowed to proceed normally.
    /// </summary>
    Allowed,

    /// <summary>
    /// Invocation is unauthorized. Returns an error to the caller without invoking the method.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// Invocation is unauthorized and the session should be immediately disconnected.
    /// </summary>
    Disconnect
}
