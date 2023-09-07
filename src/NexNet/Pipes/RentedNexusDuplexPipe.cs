﻿using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes;

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
        var manager = Interlocked.Exchange(ref Manager, null);
        
        if(manager == null)
            return default;

        return manager!.ReturnPipe(this);
    }
}