using System.IO.Pipelines;

namespace NexNet;

/// <summary>
/// Interface for Duplex Pipe
/// </summary>
public interface INexusDuplexPipe : IDuplexPipe
{
    ushort Id { get; }
}
