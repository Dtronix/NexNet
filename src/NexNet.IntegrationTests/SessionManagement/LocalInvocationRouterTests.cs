using NexNet.Invocation;
using NUnit.Framework;

namespace NexNet.IntegrationTests.SessionManagement;

[TestFixture]
internal class LocalInvocationRouterTests
{
    private LocalSessionContext _context = null!;
    private LocalInvocationRouter _router = null!;
    private LocalGroupRegistry _groupRegistry = null!;

    [SetUp]
    public void Setup()
    {
        _context = new LocalSessionContext();
        _router = new LocalInvocationRouter(_context);
        _groupRegistry = new LocalGroupRegistry(_context);
    }

    [Test]
    public async Task InvokeAllAsync_MultipleSessions_SendsToAll()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        var message = CreateTestMessage();
        await _router.InvokeAllAsync(message);

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InvokeAllAsync_NoSessions_DoesNotThrow()
    {
        var message = CreateTestMessage();
        await _router.InvokeAllAsync(message);

        // Should not throw
        Assert.Pass();
    }

    [Test]
    public async Task InvokeAllAsync_SessionSendFails_ContinuesToOthers()
    {
        var session1 = new MockNexusSession { Id = 1, ShouldFailSend = true };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        var message = CreateTestMessage();
        await _router.InvokeAllAsync(message);

        Assert.That(session1.SentMessages, Is.Empty); // Failed
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1)); // Succeeded
    }

    [Test]
    public async Task InvokeAllExceptAsync_ExcludesSpecifiedSession()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        var message = CreateTestMessage();
        await _router.InvokeAllExceptAsync(message, excludeSessionId: 1);

        Assert.That(session1.SentMessages, Is.Empty);
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InvokeAllExceptAsync_NonExistentExcludeId_SendsToAll()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        var message = CreateTestMessage();
        await _router.InvokeAllExceptAsync(message, excludeSessionId: 999);

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InvokeClientAsync_ExistingSession_SendsAndReturnsTrue()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);

        var message = CreateTestMessage();
        var result = await _router.InvokeClientAsync(message, sessionId: 1);

        Assert.That(result, Is.True);
        Assert.That(session.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InvokeClientAsync_NonExistentSession_ReturnsFalse()
    {
        var message = CreateTestMessage();
        var result = await _router.InvokeClientAsync(message, sessionId: 999);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task InvokeClientAsync_SendFails_ReturnsFalse()
    {
        var session = new MockNexusSession { Id = 1, ShouldFailSend = true };
        _context.Sessions.TryAdd(1, session);

        var message = CreateTestMessage();
        var result = await _router.InvokeClientAsync(message, sessionId: 1);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task InvokeClientsAsync_MultipleIds_SendsToAllExisting()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        var message = CreateTestMessage();
        await _router.InvokeClientsAsync(message, [1L, 2L, 999L]); // 999 doesn't exist

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InvokeClientsAsync_EmptyArray_DoesNothing()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);

        var message = CreateTestMessage();
        await _router.InvokeClientsAsync(message, []);

        Assert.That(session.SentMessages, Is.Empty);
    }

    [Test]
    public async Task InvokeGroupAsync_SendsToGroupMembers()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        var session3 = new MockNexusSession { Id = 3 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);
        _context.Sessions.TryAdd(3, session3);

        await _groupRegistry.AddToGroupAsync("mygroup", session1);
        await _groupRegistry.AddToGroupAsync("mygroup", session2);
        // session3 not in group

        var message = CreateTestMessage();
        await _router.InvokeGroupAsync(message, "mygroup");

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session3.SentMessages, Is.Empty);
    }

    [Test]
    public async Task InvokeGroupAsync_NonExistentGroup_DoesNothing()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);

        var message = CreateTestMessage();
        await _router.InvokeGroupAsync(message, "nonexistent");

        Assert.That(session.SentMessages, Is.Empty);
    }

    [Test]
    public async Task InvokeGroupAsync_WithExclude_ExcludesSpecifiedSession()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        await _groupRegistry.AddToGroupAsync("mygroup", session1);
        await _groupRegistry.AddToGroupAsync("mygroup", session2);

        var message = CreateTestMessage();
        await _router.InvokeGroupAsync(message, "mygroup", excludeSessionId: 1);

        Assert.That(session1.SentMessages, Is.Empty);
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InvokeGroupsAsync_MultipleGroups_SendsToAllGroupMembers()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        await _groupRegistry.AddToGroupAsync("group1", session1);
        await _groupRegistry.AddToGroupAsync("group2", session2);

        var message = CreateTestMessage();
        await _router.InvokeGroupsAsync(message, ["group1", "group2"]);

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InvokeGroupsAsync_SessionInMultipleGroups_ReceivesMultipleMessages()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);

        await _groupRegistry.AddToGroupAsync("group1", session);
        await _groupRegistry.AddToGroupAsync("group2", session);

        var message = CreateTestMessage();
        await _router.InvokeGroupsAsync(message, ["group1", "group2"]);

        // Session is in both groups, so receives message twice
        Assert.That(session.SentMessages, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task InvokeGroupsAsync_WithExclude_ExcludesFromAllGroups()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        await _groupRegistry.AddToGroupAsync("group1", session1);
        await _groupRegistry.AddToGroupAsync("group1", session2);
        await _groupRegistry.AddToGroupAsync("group2", session1);
        await _groupRegistry.AddToGroupAsync("group2", session2);

        var message = CreateTestMessage();
        await _router.InvokeGroupsAsync(message, ["group1", "group2"], excludeSessionId: 1);

        Assert.That(session1.SentMessages, Is.Empty);
        Assert.That(session2.SentMessages, Has.Count.EqualTo(2)); // Once per group
    }

    [Test]
    public async Task InvokeGroupsAsync_EmptyArray_DoesNothing()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);
        await _groupRegistry.AddToGroupAsync("group1", session);

        var message = CreateTestMessage();
        await _router.InvokeGroupsAsync(message, []);

        Assert.That(session.SentMessages, Is.Empty);
    }

    [Test]
    public async Task InitializeAsync_Completes()
    {
        await _router.InitializeAsync();

        // No exception = success
        Assert.Pass();
    }

    [Test]
    public async Task ShutdownAsync_Completes()
    {
        await _router.ShutdownAsync();

        // No exception = success
        Assert.Pass();
    }

    [Test]
    public async Task InvocationId_IsSetOnMessage()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);

        var message = CreateTestMessage();
        Assert.That(message.InvocationId, Is.EqualTo(0)); // Initially 0

        await _router.InvokeClientAsync(message, sessionId: 1);

        // Message should have had InvocationId set by router
        Assert.That(message.InvocationId, Is.Not.EqualTo(0));
    }

    private static MockInvocationMessage CreateTestMessage()
    {
        return new MockInvocationMessage
        {
            MethodId = 1,
            InvocationId = 0,
            Flags = NexNet.Messages.InvocationFlags.IgnoreReturn
        };
    }
}
