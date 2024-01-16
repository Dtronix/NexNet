using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Pipes;

internal class RentedNexusDuplexPipe : NexusDuplexPipe, IRentedNexusDuplexPipe
{
    public RentedNexusDuplexPipe(byte localId, INexusSession session)
        : base(0, localId, session)
    {
    }

    internal NexusPipeManager? Manager;

    public ValueTask DisposeAsync()
    {
        return Interlocked.Exchange(ref Manager, null)?.ReturnPipe(this) ?? default;
    }
}
