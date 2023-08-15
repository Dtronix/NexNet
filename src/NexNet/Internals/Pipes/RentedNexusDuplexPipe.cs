using System;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Internals.Pipes;

internal class RentedNexusDuplexPipe : NexusDuplexPipe, IRentedNexusDuplexPipe
{
    private readonly int _rentedStateId;

    public RentedNexusDuplexPipe()
    {
        _rentedStateId = StateId;
    }

    internal NexusPipeManager? Manager;

    public ValueTask DisposeAsync()
    {
        // Ensure that the state of the pipe is still the same as when it was rented.
        if(StateId != _rentedStateId)
            return default;

        var manager = Interlocked.Exchange(ref Manager, null);
        
        if(manager == null)
            return default;

        return manager!.ReturnPipe(this);
    }
}
