using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Pipes;

internal class RentedNexusDuplexPipe : NexusDuplexPipe, IRentedNexusDuplexPipe
{
    public RentedNexusDuplexPipe(byte localId, INexusSession session)
        : base(localId, session)
    {
    }

    internal NexusPipeManager? Manager;

    public ValueTask DisposeAsync()
    {
        var manager = Interlocked.Exchange(ref Manager, null);

        if (manager == null)
            return default;

        return manager!.ReturnPipe(this);
    }
}
