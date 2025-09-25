using System.Threading.Channels;
using NexNet.Collections;
using NexNet.Logging;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

[TestFixture]
internal class NexusCollectionBroadcasterTests : BaseTests
{
    private NexusCollectionBroadcaster? _broadcaster;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _broadcaster = new NexusCollectionBroadcaster(Logger);
    }

    [Test]
    public void BroadcastsToConnectedClient()
    {
        
       
    }

    private TestCollectionClient CreateTestClient()
    {
        return new TestCollectionClient(Logger);
    }

    private class NexusCollectionMessageTest : INexusCollectionMessage
    {
        public bool ReturnedToCache { get; set; }
        public NexusCollectionMessageFlags Flags { get; set; }
        public int Remaining { get; set; }
        public string Value { get; set; }
        public void ReturnToCache()
        {
            ReturnedToCache = true;
        }

        public void CompleteBroadcast()
        {
            throw new NotImplementedException();
        }

        public INexusCollectionMessage Clone()
        {
            return new NexusCollectionMessageTest()
            {
                Flags = this.Flags, Remaining = this.Remaining, Value = this.Value
            };
        }
    }

    private class TestCollectionClient : INexusCollectionClient
    {
        private readonly INexusLogger _logger;
        private static int _idCounter = 0;
        public long Id { get; }
        public Channel<NexusBroadcastMessageWrapper> MessageBuffer { get; }
        public CancellationToken CompletionToken { get; }
        public INexusLogger? Logger { get; }

        public TestCollectionClient(INexusLogger logger)
        {
            _logger = logger;
            Id = Interlocked.Increment(ref _idCounter);
            
            MessageBuffer = Channel.CreateUnbounded<NexusBroadcastMessageWrapper>(new  UnboundedChannelOptions()
            {
                SingleReader = true,
                SingleWriter = true, 
            });

            
        }
        public ValueTask CompletePipe()
        {
            throw new NotImplementedException();
        }

        public ValueTask<bool> WriteAsync(INexusCollectionMessage message, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
