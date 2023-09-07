using System.Threading.Tasks;

namespace NexNet.Pipes;

internal interface IPipeStateManager
{
    /// <summary>
    /// Id of the pipe.
    /// </summary>
    ushort Id { get; }

    /// <summary>
    /// Notifies the other end of the pipe that the state has changed.
    /// </summary>
    /// <returns>Task which completes when the change in state has been sent.</returns>
    ValueTask NotifyState();

    /// <summary>
    /// Updates the state of the NexusDuplexPipe .
    /// </summary>
    /// <param name="updatedState">The state to update to.</param>
    /// <param name="remove">Removes the state from the pipe. Otherwise it is added.</param>
    /// <returns>True if the state changed, false if it did not change.</returns>
    bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false);

    /// <summary>
    /// State of the pipe.
    /// </summary>
    NexusDuplexPipe.State CurrentState { get; }
}
