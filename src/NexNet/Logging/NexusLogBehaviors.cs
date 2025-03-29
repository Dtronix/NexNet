using System;

namespace NexNet.Logging;

/// <summary>
/// Default behaviour options for the nexus logging system.
/// </summary>
[Flags]
public enum NexusLogBehaviors : int
{
    /// <summary>
    /// Default behaviour for all logging.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Set to log all proxy nexus invocations on the nexus as Info instead of Debug.
    /// </summary>
    ProxyInvocationsLogAsInfo = 1 << 0,

    /// <summary>
    /// Set to log all local nexus invocations on the nexus as Info instead of Debug.
    /// </summary>
    LocalInvocationsLogAsInfo = 1 << 1,
}
