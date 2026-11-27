namespace NexNet.Inspector.Core.Model;

/// <summary>Which way bytes moved on the tapped transport, seen from the tapped endpoint.</summary>
/// <remarks>Shared by the frame parser, the recording format and the tap so every layer names a direction the same way.</remarks>
public enum TapDirection : byte
{
    /// <summary>Bytes the tapped endpoint read from its transport.</summary>
    Inbound = 0,

    /// <summary>Bytes the tapped endpoint wrote to its transport.</summary>
    Outbound = 1,
}
