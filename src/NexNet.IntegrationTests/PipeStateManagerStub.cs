using NexNet.Internals.Pipes;

namespace NexNet.IntegrationTests;

internal class PipeStateManagerStub : IPipeStateManager
{
    private NexusDuplexPipe.State _currentState;

    public PipeStateManagerStub(NexusDuplexPipe.State initialState = NexusDuplexPipe.State.Unset)
    {

    }

    public ushort Id { get; set; }
    public ValueTask NotifyState()
    {
        return default;
    }

    public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
    {
        if (remove)
        {
            // Remove the state from the current state.
            _currentState &= ~updatedState;
        }
        else
        {
            // Add the state to the current state.
            _currentState |= updatedState;
        }
        return true;
    }

    public NexusDuplexPipe.State CurrentState => _currentState;
}
