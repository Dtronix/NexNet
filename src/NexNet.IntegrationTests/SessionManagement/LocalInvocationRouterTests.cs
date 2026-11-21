using NexNet.Invocation;

namespace NexNet.IntegrationTests.SessionManagement;
internal class LocalInvocationRouterTests
{
    private LocalSessionContext _context = null!;
    private LocalInvocationRouter _router = null!;
    private LocalGroupRegistry _groupRegistry = null!;

    [Before(HookType.Test)]
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

        await Assert.That(session1.SentMessages).Count().IsEqualTo(1);
        await Assert.That(session2.SentMessages).Count().IsEqualTo(1);
    }

    [Test]
    public async Task InvokeAllAsync_NoSessions_DoesNotThrow()
    {
        var message = CreateTestMessage();
        await _router.InvokeAllAsync(message);

        // Should not throw
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

        await Assert.That(session1.SentMessages).IsEmpty(); // Failed
        await Assert.That(session2.SentMessages).Count().IsEqualTo(1); // Succeeded
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

        await Assert.That(session1.SentMessages).IsEmpty();
        await Assert.That(session2.SentMessages).Count().IsEqualTo(1);
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

        await Assert.That(session1.SentMessages).Count().IsEqualTo(1);
        await Assert.That(session2.SentMessages).Count().IsEqualTo(1);
    }

    [Test]
    public async Task InvokeClientAsync_ExistingSession_SendsAndReturnsTrue()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);

        var message = CreateTestMessage();
        var result = await _router.InvokeClientAsync(message, sessionId: 1);

        await Assert.That(result).IsTrue();
        await Assert.That(session.SentMessages).Count().IsEqualTo(1);
    }

    [Test]
    public async Task InvokeClientAsync_NonExistentSession_ReturnsFalse()
    {
        var message = CreateTestMessage();
        var result = await _router.InvokeClientAsync(message, sessionId: 999);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task InvokeClientAsync_SendFails_ReturnsFalse()
    {
        var session = new MockNexusSession { Id = 1, ShouldFailSend = true };
        _context.Sessions.TryAdd(1, session);

        var message = CreateTestMessage();
        var result = await _router.InvokeClientAsync(message, sessionId: 1);

        await Assert.That(result).IsFalse();
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

        await Assert.That(session1.SentMessages).Count().IsEqualTo(1);
        await Assert.That(session2.SentMessages).Count().IsEqualTo(1);
    }

    [Test]
    public async Task InvokeClientsAsync_EmptyArray_DoesNothing()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);

        var message = CreateTestMessage();
        await _router.InvokeClientsAsync(message, []);

        await Assert.That(session.SentMessages).IsEmpty();
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

        await Assert.That(session1.SentMessages).Count().IsEqualTo(1);
        await Assert.That(session2.SentMessages).Count().IsEqualTo(1);
        await Assert.That(session3.SentMessages).IsEmpty();
    }

    [Test]
    public async Task InvokeGroupAsync_NonExistentGroup_DoesNothing()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);

        var message = CreateTestMessage();
        await _router.InvokeGroupAsync(message, "nonexistent");

        await Assert.That(session.SentMessages).IsEmpty();
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

        await Assert.That(session1.SentMessages).IsEmpty();
        await Assert.That(session2.SentMessages).Count().IsEqualTo(1);
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

        await Assert.That(session1.SentMessages).Count().IsEqualTo(1);
        await Assert.That(session2.SentMessages).Count().IsEqualTo(1);
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
        await Assert.That(session.SentMessages).Count().IsEqualTo(2);
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

        await Assert.That(session1.SentMessages).IsEmpty();
        await Assert.That(session2.SentMessages).Count().IsEqualTo(2); // Once per group
    }

    [Test]
    public async Task InvokeGroupsAsync_EmptyArray_DoesNothing()
    {
        var session = new MockNexusSession { Id = 1 };
        _context.Sessions.TryAdd(1, session);
        await _groupRegistry.AddToGroupAsync("group1", session);

        var message = CreateTestMessage();
        await _router.InvokeGroupsAsync(message, []);

        await Assert.That(session.SentMessages).IsEmpty();
    }

    [Test]
    public async Task InitializeAsync_Completes()
    {
        await _router.InitializeAsync();

        // No exception = success
    }

    [Test]
    public async Task ShutdownAsync_Completes()
    {
        await _router.ShutdownAsync();

        // No exception = success
    }

    [Test]
    public async Task Message_IsSentUnchangedToEverySession()
    {
        // One message is written to every session, possibly concurrently, so the router must not modify it.
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        var message = CreateTestMessage();
        await _router.InvokeAllAsync(message);

        await Assert.That(message.InvocationId).IsEqualTo(0);
        await Assert.That(session1.SentMessages).IsEquivalentTo(new object[] { message }, CollectionOrdering.Matching);
        await Assert.That(session2.SentMessages).IsEquivalentTo(new object[] { message }, CollectionOrdering.Matching);
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