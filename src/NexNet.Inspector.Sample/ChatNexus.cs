using System.Collections.Concurrent;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;

namespace NexNet.Inspector.Sample;

// The "Chat" nexus pair behind the inspector screens: an attachment large enough to stream as five MessageChunks, a raw
// FileUpload pipe, a typed LogLine channel, the synchronized Users collection, and a SendMessage that can be canceled.

/// <summary>Severity of a <see cref="LogLine"/>.</summary>
public enum ChatLogLevel : byte
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>Presence shown in the Users collection.</summary>
public enum UserStatus : byte
{
    Online = 0,
    Away = 1,
    Busy = 2,
}

/// <summary>A joined user; the Users collection lists their IDs.</summary>
[NexusObject]
public sealed class ChatUser
{
    [NexusKey(0)] public long Id { get; set; }
    [NexusKey(1)] public string Name { get; set; } = "";
    [NexusKey(2)] public UserStatus Status { get; set; }
}

/// <summary>A chat line.</summary>
[NexusObject]
public sealed class ChatMessage
{
    [NexusKey(0)] public string Room { get; set; } = "";
    [NexusKey(1)] public string Author { get; set; } = "";
    [NexusKey(2)] public string Text { get; set; } = "";
    [NexusKey(3)] public DateTime SentUtc { get; set; }
}

/// <summary>A file posted to a room; its data makes the invocation a large (chunked) message.</summary>
[NexusObject]
public sealed class Attachment
{
    [NexusKey(0)] public string Room { get; set; } = "";
    [NexusKey(1)] public string FileName { get; set; } = "";
    [NexusKey(2)] public string ContentType { get; set; } = "";
    [NexusKey(3)] public byte[] Data { get; set; } = [];
}

/// <summary>What the server stored for an attachment.</summary>
[NexusObject]
public sealed class AttachmentReceipt
{
    [NexusKey(0)] public int Length { get; set; }
    [NexusKey(1)] public uint Checksum { get; set; }
    [NexusKey(2)] public string Url { get; set; } = "";
}

/// <summary>One server log line, streamed over the TailLogs channel.</summary>
[NexusObject]
public sealed class LogLine
{
    [NexusKey(0)] public long Sequence { get; set; }
    [NexusKey(1)] public ChatLogLevel Level { get; set; }
    [NexusKey(2)] public string Source { get; set; } = "";
    [NexusKey(3)] public string Text { get; set; } = "";
}

public interface IChatServer
{
    /// <summary>Adds the caller to <see cref="Users"/>.</summary>
    ValueTask<ChatUser> Join(string name);

    /// <summary>Posts a message to everyone; a text starting with <c>/slow</c> waits until canceled.</summary>
    ValueTask<bool> SendMessage(ChatMessage message, CancellationToken cancellationToken);

    /// <summary>Stores an attachment.</summary>
    ValueTask<AttachmentReceipt> SendAttachment(Attachment attachment);

    /// <summary>Reads a file from the raw pipe until the client completes it.</summary>
    ValueTask UploadFile(string fileName, INexusDuplexPipe pipe);

    /// <summary>Streams <paramref name="count"/> log lines at or above <paramref name="minimum"/>, then completes.</summary>
    ValueTask TailLogs(ChatLogLevel minimum, int count, INexusDuplexChannel<LogLine> channel);

    /// <summary>
    /// IDs of the joined users. A list of a primitive type: a list of a [NexusObject] type cannot send a non-empty reset
    /// to a second client yet (no formatter for <c>Memory&lt;T&gt;</c> in <c>NexusListServer.SnapshotMessages</c>).
    /// </summary>
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    INexusList<long> Users { get; }
}

public interface IChatClient
{
    /// <summary>A message posted to a room.</summary>
    ValueTask ReceiveMessage(ChatMessage message);
}

/// <summary>The Chat server. Accepts any non-empty token.</summary>
[Nexus<IChatServer, IChatClient>(NexusType = NexusType.Server)]
public partial class ChatServerNexus
{
    /// <summary>Bytes received per uploaded file name, for the scenario's own checks.</summary>
    public static readonly ConcurrentDictionary<string, long> Uploads = new();

    private ChatUser? _user;

    public async ValueTask<ChatUser> Join(string name)
    {
        _user = new ChatUser { Id = Context.Id, Name = name, Status = UserStatus.Online };
        await Users.AddAsync(_user.Id).ConfigureAwait(false);
        return _user;
    }

    public async ValueTask<bool> SendMessage(ChatMessage message, CancellationToken cancellationToken)
    {
        if (message.Text.StartsWith("/slow", StringComparison.Ordinal))
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

        await Context.Clients.All.ReceiveMessage(message).ConfigureAwait(false);
        return true;
    }

    public ValueTask<AttachmentReceipt> SendAttachment(Attachment attachment)
    {
        uint checksum = 0;
        foreach (var b in attachment.Data)
            checksum = (checksum * 31) + b;
        return new ValueTask<AttachmentReceipt>(new AttachmentReceipt
        {
            Length = attachment.Data.Length,
            Checksum = checksum,
            Url = $"chat://{attachment.Room}/{attachment.FileName}",
        });
    }

    public async ValueTask UploadFile(string fileName, INexusDuplexPipe pipe)
    {
        long total = 0;
        while (true)
        {
            var read = await pipe.Input.ReadAsync().ConfigureAwait(false);
            total += read.Buffer.Length;
            pipe.Input.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted || read.IsCanceled)
                break;
        }

        Uploads[fileName] = total;
    }

    public async ValueTask TailLogs(ChatLogLevel minimum, int count, INexusDuplexChannel<LogLine> channel)
    {
        var writer = await channel.GetWriterAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            if (!await writer.WriteAsync(ChatLog.Line(i, minimum)).ConfigureAwait(false))
                break;
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? authenticationToken) =>
        new(authenticationToken is { Length: > 0 } ? new DefaultIdentity() : null);

    protected override async ValueTask OnDisconnected(DisconnectReason reason)
    {
        if (_user == null)
            return;

        var users = Users;
        for (var i = 0; i < users.Count; i++)
        {
            if (users[i] == _user.Id)
            {
                await users.RemoveAtAsync(i).ConfigureAwait(false);
                break;
            }
        }
    }
}

/// <summary>The Chat client.</summary>
[Nexus<IChatClient, IChatServer>(NexusType = NexusType.Client)]
public partial class ChatClientNexus
{
    private int _received;

    /// <summary>Messages received from the server.</summary>
    public int Received => Volatile.Read(ref _received);

    public ValueTask ReceiveMessage(ChatMessage message)
    {
        Interlocked.Increment(ref _received);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Deterministic log lines: the same index gives the same bytes, so recordings differ only in timing.</summary>
internal static class ChatLog
{
    private static readonly string[] Sources = ["chat.rooms", "chat.auth", "chat.storage", "chat.presence"];

    public static LogLine Line(long sequence, ChatLogLevel minimum)
    {
        var level = (ChatLogLevel)Math.Max((int)minimum, (int)(sequence % 5));
        var source = Sources[sequence % Sources.Length];
        return new LogLine
        {
            Sequence = sequence,
            Level = level,
            Source = source,
            Text = $"{source} event {sequence}: room general has {3 + (sequence % 7)} members; queue depth {sequence % 13}; "
                   + "flushed attachment index and presence snapshot to the store without errors.",
        };
    }
}
