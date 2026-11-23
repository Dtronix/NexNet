using NexNet.Messages;

namespace NexNet.Inspector.Core.Framing;

/// <summary>The framing shape of each message type (protocol specification §2, §3).</summary>
public static class FrameLayout
{
    /// <summary>Length of the connection preamble (§1).</summary>
    public const int PreambleLength = 8;

    /// <summary>The preamble's magic bytes, <c>N n P DC4</c>.</summary>
    public static ReadOnlySpan<byte> PreambleMagic => [0x4E, 0x6E, 0x50, 0x14];

    /// <summary>
    /// True for every code of the disconnect range 20–38, assigned or not: NexNet reads any of them as a disconnect
    /// reason before anything else, so they all frame as a single body-less byte.
    /// </summary>
    public static bool IsDisconnect(byte type) => type is >= 20 and <= 38;

    /// <summary>Gets the header and post-header lengths of a type byte.</summary>
    /// <param name="type">The type byte.</param>
    /// <param name="headerLength">1 for Ping and disconnects, otherwise 3 (type plus uint16 LE body length).</param>
    /// <param name="postHeaderLength">2 for a pipe ID, 3 for a MessageChunk's invocation ID and kind, otherwise 0.</param>
    /// <returns>False when the byte is no message type of the protocol.</returns>
    public static bool TryGetLayout(byte type, out byte headerLength, out byte postHeaderLength)
    {
        postHeaderLength = 0;
        if (type == (byte)MessageType.Ping || IsDisconnect(type))
        {
            headerLength = 1;
            return true;
        }

        headerLength = 3;
        switch ((MessageType)type)
        {
            case MessageType.DuplexPipeWrite:
            case MessageType.PipeWindowUpdate:
                postHeaderLength = 2;
                return true;
            case MessageType.MessageChunk:
                postHeaderLength = 3;
                return true;
            case MessageType.ClientGreeting:
            case MessageType.ClientGreetingReconnection:
            case MessageType.ServerGreeting:
            case MessageType.Invocation:
            case MessageType.InvocationCancellation:
            case MessageType.InvocationResult:
            case MessageType.MessageStart:
            case MessageType.MessageAbort:
            case MessageType.InvocationCredit:
            case MessageType.DuplexPipeUpdateState:
                return true;
            default:
                headerLength = 0;
                return false;
        }
    }
}
