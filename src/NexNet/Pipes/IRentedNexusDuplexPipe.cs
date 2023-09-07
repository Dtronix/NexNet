using System;

namespace NexNet.Pipes;

/// <summary>
/// Interface for rented duplex pipe.  This interface is used to return the pipe to
/// the pipe manager once disposed via the <see cref="IAsyncDisposable.DisposeAsync"/> method.
/// </summary>
public interface IRentedNexusDuplexPipe : INexusDuplexPipe, IAsyncDisposable
{

}
