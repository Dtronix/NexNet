using NexNet.Pipes;

namespace NexNet.Inspector.Sample;

// The nexus pair behind the findings fixtures (findings-*.nntl): a call that can be slow and a pipe reader that can lag
// behind its writer, so the recordings hold a slow call, window stalls and a result whose request came before the
// capture. Kept apart from the Chat nexus, whose schema hash the Chat fixtures pin.

public interface IFindingsServer
{
    /// <summary>Waits <paramref name="delayMs"/> (0 returns at once), then returns it.</summary>
    ValueTask<int> Work(int delayMs);

    /// <summary>Reads the pipe to its end, waiting <paramref name="readDelayMs"/> after each read: a slow reader.</summary>
    ValueTask Upload(int readDelayMs, INexusDuplexPipe pipe);
}

public interface IFindingsClient
{
    /// <summary>Unused; a client nexus needs a method.</summary>
    ValueTask Notify(string text);
}

/// <summary>The findings server.</summary>
[Nexus<IFindingsServer, IFindingsClient>(NexusType = NexusType.Server)]
public partial class FindingsServerNexus
{
    public async ValueTask<int> Work(int delayMs)
    {
        if (delayMs > 0)
            await Task.Delay(delayMs).ConfigureAwait(false);
        return delayMs;
    }

    public async ValueTask Upload(int readDelayMs, INexusDuplexPipe pipe)
    {
        while (true)
        {
            var read = await pipe.Input.ReadAsync().ConfigureAwait(false);
            pipe.Input.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted || read.IsCanceled)
                break;
            await Task.Delay(readDelayMs).ConfigureAwait(false);
        }
    }
}

/// <summary>The findings client.</summary>
[Nexus<IFindingsClient, IFindingsServer>(NexusType = NexusType.Client)]
public partial class FindingsClientNexus
{
    public ValueTask Notify(string text) => ValueTask.CompletedTask;
}
