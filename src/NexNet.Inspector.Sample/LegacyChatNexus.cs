using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes;

namespace NexNet.Inspector.Sample;

// An earlier version of the Chat API (plain strings, no attachments or logs). Its schema hash differs from the current
// Chat schema, so recordings made with it exercise the open-recording "schema hash mismatch" path.

public interface ILegacyChatServer
{
    ValueTask<bool> SendMessage(string room, string text, CancellationToken cancellationToken);

    ValueTask UploadFile(string fileName, INexusDuplexPipe pipe);

    [NexusCollection(NexusCollectionMode.ServerToClient)]
    INexusList<long> Users { get; }
}

public interface ILegacyChatClient
{
    ValueTask ReceiveMessage(string room, string text);
}

/// <summary>The legacy Chat server.</summary>
[Nexus<ILegacyChatServer, ILegacyChatClient>(NexusType = NexusType.Server)]
public partial class LegacyChatServerNexus
{
    public async ValueTask<bool> SendMessage(string room, string text, CancellationToken cancellationToken)
    {
        await Context.Clients.All.ReceiveMessage(room, text).ConfigureAwait(false);
        return true;
    }

    public async ValueTask UploadFile(string fileName, INexusDuplexPipe pipe)
    {
        while (true)
        {
            var read = await pipe.Input.ReadAsync().ConfigureAwait(false);
            pipe.Input.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted || read.IsCanceled)
                break;
        }
    }

    protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? authenticationToken) =>
        new(authenticationToken is { Length: > 0 } ? new DefaultIdentity() : null);

    protected override async ValueTask OnConnected(bool isReconnected)
    {
        await Users.AddAsync(Context.Id).ConfigureAwait(false);
    }
}

/// <summary>The legacy Chat client.</summary>
[Nexus<ILegacyChatClient, ILegacyChatServer>(NexusType = NexusType.Client)]
public partial class LegacyChatClientNexus
{
    public ValueTask ReceiveMessage(string room, string text) => ValueTask.CompletedTask;
}
