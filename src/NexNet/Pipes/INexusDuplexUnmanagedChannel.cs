namespace NexNet.Pipes;

/// <summary>
/// Represents a duplex channel utilizing a <see cref="INexusDuplexPipe"/>. This channel allows for bidirectional communication, 
/// meaning that data can be both sent and received. The type of data that can be transmitted is defined by the generic parameter T.
/// </summary>
/// <typeparam name="T">The type of the data that can be transmitted through the channel.</typeparam>
public interface INexusDuplexUnmanagedChannel<T> : INexusDuplexChannel<T>
    where T : unmanaged
{

}
