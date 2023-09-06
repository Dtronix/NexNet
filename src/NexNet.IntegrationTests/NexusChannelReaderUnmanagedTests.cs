using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NexNet.Internals.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal class NexusChannelReaderUnmanagedTests
{
    private class DummyPipeStateManager : IPipeStateManager
    {
        public ushort Id { get; }
        public ValueTask NotifyState()
        {
            return default;
        }

        public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
        {
            CurrentState |= updatedState;
            return true;
        }

        public NexusDuplexPipe.State CurrentState { get; private set; } = NexusDuplexPipe.State.Ready;
    }

    [Test]
    public async Task ReadsData()
    {
        var duplexPipe = new NexusDuplexPipe();
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());

        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);

        var result = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(1);
    }
}
