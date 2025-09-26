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
    
    [TestCase(true)]
    [TestCase(false)]
    public async Task BroadcastReturnsMessageToCache(bool hasSource)
    {
        using var bl = new BroadcasterLifetime(Logger);
        var client1 = CreateTestClient();
        bl.Broadcaster.AddClientAsync(client1);
        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, hasSource ? client1 : null);

        await message.ReturnedToCacheTask.Timeout(1);

        Assert.That(message.ReturnedToCacheCount, Is.EqualTo(1));
        Assert.That(message.SourceClientClone, hasSource ? Is.Not.Null : Is.Null);
        if(hasSource)
            Assert.That(message.SourceClientClone.ReturnedToCacheCount, Is.EqualTo(1));
    }
    
    [TestCase(true)]
    [TestCase(false)]
    public async Task BroadcastReturnsMessageToCacheWithMultipleClients(bool hasSource)
    {
        using var bl = new BroadcasterLifetime(Logger);

        var clients = new TestCollectionClient[100];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = CreateTestClient();
            bl.Broadcaster.AddClientAsync(clients[i]);
        }

        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, hasSource ? clients[0] : null);

        await message.ReturnedToCacheTask.Timeout(1);

        Assert.That(message.ReturnedToCacheCount, Is.EqualTo(1));
        Assert.That(message.SourceClientClone, hasSource ? Is.Not.Null : Is.Null);
        if(hasSource)
            Assert.That(message.SourceClientClone.ReturnedToCacheCount, Is.EqualTo(1));
    }
    
    [TestCase(true)]
    [TestCase(false)]
    public async Task BroadcastMessagesAllClients(bool hasSource)
    {
        using var bl = new BroadcasterLifetime(Logger);

        var clients = new TestCollectionClient[100];
        for (int i = 0; i < clients.Length; i++)
            bl.Broadcaster.AddClientAsync(clients[i] = CreateTestClient());
        
        var clientComplete = clients.Select(c => c.EventCount(ClientEvent.SendAsync, 1).Complete).ToArray();

        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, hasSource ? clients[0] : null);
        
        await Task.WhenAll(clientComplete).Timeout(1);

        Assert.That(clients, Has.All.Matches<TestCollectionClient>(c => c.BufferWrites.Count == 1));
    }
    
    [TestCase(true)]
    [TestCase(false)]
    public async Task BroadcastMessagesSetsFlags(bool hasSource)
    {
        using var bl = new BroadcasterLifetime(Logger);

        var clients = new TestCollectionClient[10];
        for (int i = 0; i < clients.Length; i++)
            bl.Broadcaster.AddClientAsync(clients[i] = CreateTestClient());
        
        var clientComplete = clients.Select(c => c.EventCount(ClientEvent.SendAsync, 1).Complete).ToArray();

        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, hasSource ? clients[0] : null);
        
        await Task.WhenAll(clientComplete).Timeout(1);

        if (hasSource)
        {
            Assert.That(clients[0].Sends[0].Flags, Is.EqualTo(NexusCollectionMessageFlags.Ack));
            Assert.That(clients[1..], Has.All.Matches<TestCollectionClient>(
                c => c.Sends[0].Flags != NexusCollectionMessageFlags.Ack));
        }
        else
        {
            Assert.That(clients, Has.All.Matches<TestCollectionClient>(
                c => c.Sends[0].Flags != NexusCollectionMessageFlags.Ack));
        }
    }
    
    [TestCase(true)]
    [TestCase(false)]
    public async Task BroadcastCompletesPipeOnFullBuffer(bool hasSource)
    {
        using var bl = new BroadcasterLifetime(Logger);

        var clients = new TestCollectionClient[2];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = CreateTestClient();
            clients[i].StopBuffers = true;
            bl.Broadcaster.AddClientAsync(clients[i]);
        }

        var clientComplete = clients.Select(
            c => c.EventCount(ClientEvent.CompletePipe, 1).Complete).ToArray();

        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, hasSource ? clients[0] : null);
        
        await Task.WhenAll(clientComplete).Timeout(1);

        Assert.That(clients, Has.All.Matches<TestCollectionClient>(c => c.CompletePipeFired));
    }
    
    [TestCase(true)]
    [TestCase(false)]
    public async Task BroadcastCompletesPipeOnSendException(bool hasSource)
    {
        using var bl = new BroadcasterLifetime(Logger);

        var clients = new TestCollectionClient[2];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = CreateTestClient();
            clients[i].ThrowOnSend = true;
            bl.Broadcaster.AddClientAsync(clients[i]);
        }

        var clientComplete = clients.Select(
            c => c.EventCount(ClientEvent.CompletePipe, 1).Complete).ToArray();

        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, hasSource ? clients[0] : null);
        
        await Task.WhenAll(clientComplete).Timeout(1);

        Assert.That(clients, Has.All.Matches<TestCollectionClient>(c => c.CompletePipeFired));
    }
    
    [TestCase(true)]
    [TestCase(false)]
    public async Task BroadcastCompletesPipeOnFailedSend(bool hasSource)
    {
        using var bl = new BroadcasterLifetime(Logger);

        var clients = new TestCollectionClient[2];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = CreateTestClient();
            clients[i].StopSends = true;
            bl.Broadcaster.AddClientAsync(clients[i]);
        }

        var clientComplete = clients.Select(
            c => c.EventCount(ClientEvent.CompletePipe, 1).Complete).ToArray();

        var message = new NexusCollectionMessageTest();
        await bl.Broadcaster.BroadcastAsync(message, hasSource ? clients[0] : null);
        
        await Task.WhenAll(clientComplete).Timeout(1);

        Assert.That(clients, Has.All.Matches<TestCollectionClient>(c => c.CompletePipeFired));
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
    
    private enum ClientEvent
    {
        CompletePipe,
        SendAsync,
        BufferTryWrite,
        BufferRead,
        BeginSendAsync,
        BeginBufferTryWrite
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
        public bool ThrowOnSend = false;

        public event Action<ClientEvent>? OnEvent; 
  
        
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
            OnEvent?.Invoke(ClientEvent.CompletePipe);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> SendAsync(INexusCollectionMessage message, CancellationToken ct = default)
        {
            OnEvent?.Invoke(ClientEvent.BeginSendAsync);
            
            if(ThrowOnSend)
                throw new Exception("Test ThrowOnSend");
            
            if (StopSends)
                return new ValueTask<bool>(false);
            
            lock (Sends)
                Sends.Add(message);
            
            OnEvent?.Invoke(ClientEvent.SendAsync);

            return new ValueTask<bool>(true);
        }

        public bool BufferTryWrite(INexusBroadcastMessageWrapper message)
        {
            OnEvent?.Invoke(ClientEvent.BeginBufferTryWrite);
            if(StopBuffers)
                return false;
            
            lock(BufferWrites)
                BufferWrites.Add(message);
            
            OnEvent?.Invoke(ClientEvent.BufferTryWrite);
            
            return _messageBuffer.Writer.TryWrite(message);
        }

        public IAsyncEnumerable<INexusBroadcastMessageWrapper> BufferRead(CancellationToken ct = default)
        {
            OnEvent?.Invoke(ClientEvent.BufferRead);
            return _messageBuffer.Reader.ReadAllAsync(ct);
        }

        public OnEventCounter EventCount(ClientEvent eventAction, int targetCount)
        {
            return new OnEventCounter(this, eventAction, targetCount);
        }

        public class OnEventCounter
        {
            private TestCollectionClient _client;
            private readonly ClientEvent _eventAction;
            private readonly int _targetCount;
            private int _fireCount;
            private readonly TaskCompletionSource _tcs;
            
            public Task Complete => _tcs.Task;

            public OnEventCounter(TestCollectionClient client, ClientEvent eventAction, int targetCount)
            {
                _client = client;
                _client.OnEvent += ClientOnOnEvent;
                _eventAction = eventAction;
                _targetCount = targetCount;
                _tcs = new TaskCompletionSource();
            }

            private void ClientOnOnEvent(ClientEvent ev)
            {
                if (ev == _eventAction && Interlocked.Increment(ref _fireCount) == _targetCount)
                    _tcs.TrySetResult();
            }
        }
    }
}
