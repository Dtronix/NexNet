using System.Buffers;
using NexNet.Diagnostics;
using NexNet.Messages;

namespace NexNet.Inspector.Sample;

/// <summary>
/// A client tap that slows the flush of each MessageChunk while enabled, optionally in front of another tap (the
/// client's inspector).
/// </summary>
/// <remarks>
/// On loopback a 300 KB attachment goes out in well under a millisecond, so nothing could interleave with its chunks.
/// Holding each chunk's flush emulates a slow link: the session gives up its write lock between chunks (spec §6.1),
/// so the FileUpload pipe's frames and the ping timer's Ping land between them, as in the inspect-live screen.
/// </remarks>
public sealed class ChunkPacer(INexusTap? inner = null) : INexusTap
{
    private volatile bool _enabled;

    /// <summary>The hold per chunk flush.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>True while chunk flushes are held.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    INexusSessionTap? INexusTap.SessionStarted(in NexusTapSessionInfo session) =>
        new PacedSession(this, inner?.SessionStarted(session));

    private sealed class PacedSession(ChunkPacer pacer, INexusSessionTap? inner) : INexusSessionTap
    {
        public ValueTask OnRead(ReadOnlySequence<byte> data, long timestamp) =>
            inner?.OnRead(data, timestamp) ?? ValueTask.CompletedTask;

        public ValueTask OnWrite(ReadOnlySequence<byte> data, long timestamp)
        {
            var forwarded = inner?.OnWrite(data, timestamp) ?? ValueTask.CompletedTask;
            if (!pacer._enabled || data.IsEmpty || data.FirstSpan[0] != (byte)MessageType.MessageChunk)
                return forwarded;
            return HoldAsync(forwarded, pacer.Delay);
        }

        public void OnConnected(long sessionId) => inner?.OnConnected(sessionId);

        public void OnClosed(DisconnectReason reason) => inner?.OnClosed(reason);

        private static async ValueTask HoldAsync(ValueTask forwarded, TimeSpan delay)
        {
            await forwarded.ConfigureAwait(false);
            await Task.Delay(delay).ConfigureAwait(false);
        }
    }
}
