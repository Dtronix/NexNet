using System.Threading.Channels;
using NexNet.Collections;
using NexNet.IntegrationTests.Pipes;
using NexNet.Logging;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

[TestFixture]
internal class NexusCollectionBroadcasterTests : BaseTests
{

    [SetUp]
    public virtual void SetUp()
    {
        LoggerMode = BasePipeTests.LogMode.OnTestFail;
        base.SetUp();
    }

    [Test]
    public async Task BroadcastReturnsMessageToCache()
    {
        using var bl = new BroadcasterLifetime(Logger);
        var client1 = CreateTestClient();
        bl.Broadcaster.AddClientAsync(client1);
        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, client1);

        await message.ReturnedToCacheTask.Timeout(1);

        Assert.That(message.ReturnedToCacheCount, Is.EqualTo(1));
        Assert.That(message.SourceClientClone, Is.Not.Null);
        Assert.That(message.SourceClientClone.ReturnedToCacheCount, Is.EqualTo(1));
    }
    
    [Test]
    public async Task BroadcastReturnsMessageToCacheWithNoClient_NoSource()
    {
        using var bl = new BroadcasterLifetime(Logger);
        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, null);

        await message.ReturnedToCacheTask.Timeout(1);

        Assert.That(message.ReturnedToCacheCount, Is.EqualTo(1));
        Assert.That(message.SourceClientClone, Is.Null);
    }

    
    [Test]
    public async Task BroadcastReturnsMessageToCacheWithMultipleClients_NoSource()
    {
        using var bl = new BroadcasterLifetime(Logger);

        for (int i = 0; i < 40; i++)
            bl.Broadcaster.AddClientAsync(CreateTestClient());
        
        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, null);

        await message.ReturnedToCacheTask.Timeout(1);

        Assert.That(message.ReturnedToCacheCount, Is.EqualTo(1));
        Assert.That(message.SourceClientClone, Is.Null);
    }

    private TestCollectionClient CreateTestClient()
    {
        return new TestCollectionClient(Logger);
    }

    private class BroadcasterLifetime : IDisposable
    {
        private CancellationTokenSource _cts;

        public NexusCollectionBroadcaster Broadcaster { get; }

        public BroadcasterLifetime(INexusLogger logger)
        {
            Broadcaster = new NexusCollectionBroadcaster(logger);
            _cts = new CancellationTokenSource();
            Broadcaster.Run(_cts.Token);
        }
        
        public void Dispose()
        {
            _cts.Dispose();
        }
    }

    private class NexusCollectionMessageTest : INexusCollectionMessage
    {
        public int ReturnedToCacheCount;
        public NexusCollectionMessageFlags Flags { get; set; }
        public int Remaining { get; set; }
        public string Value { get; set; }

        private TaskCompletionSource _returnedToCacheTaskTcs = new();
        public Task ReturnedToCacheTask => _returnedToCacheTaskTcs.Task;
        
        public  NexusCollectionMessageTest? SourceClientClone { get; set; }
        
        public void ReturnToCache()
        {
            Interlocked.Increment(ref ReturnedToCacheCount);
            _returnedToCacheTaskTcs.TrySetResult();
        }

        public void CompleteBroadcast()
        {
            // To be removed.
            throw new NotImplementedException();
        }

        public INexusCollectionMessage Clone()
        {
            return SourceClientClone = new NexusCollectionMessageTest()
            {
                Flags = this.Flags, Remaining = this.Remaining, Value = this.Value
            };
        }
    }

    private class TestCollectionClient : INexusCollectionClient
    {
        private static int _idCounter;
        public bool CompletePipeFired;
        private readonly Channel<INexusBroadcastMessageWrapper> _messageBuffer;

        public long Id { get; }
        public CancellationToken CompletionToken { get; }
        public INexusLogger? Logger { get; }

        public List<INexusBroadcastMessageWrapper> BufferWrites { get; } = new();
        public List<INexusCollectionMessage> Sends { get; } = new();

        public bool StopSends = false;
        public bool StopBuffers = false;
  
        
        public TestCollectionClient(INexusLogger logger)
        {
            Logger = logger;
            Id = Interlocked.Increment(ref _idCounter);
            
            _messageBuffer = Channel.CreateUnbounded<INexusBroadcastMessageWrapper>(new  UnboundedChannelOptions()
            {
                SingleReader = true,
                SingleWriter = true, 
            });
        }

        public ValueTask CompletePipe()
        {
            CompletePipeFired = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> SendAsync(INexusCollectionMessage message, CancellationToken ct = default)
        {
            if (StopSends)
                return new ValueTask<bool>(false);
            
            lock (Sends)
                Sends.Add(message);

            return new ValueTask<bool>(true);
        }

        public bool BufferTryWrite(INexusBroadcastMessageWrapper message)
        {
            if(StopBuffers)
                return false;
            
            lock(BufferWrites)
                BufferWrites.Add(message);
            
            return _messageBuffer.Writer.TryWrite(message);
        }

        public IAsyncEnumerable<INexusBroadcastMessageWrapper> BufferRead(CancellationToken ct = default)
        {
            return _messageBuffer.Reader.ReadAllAsync(ct);
        }
    }
}
