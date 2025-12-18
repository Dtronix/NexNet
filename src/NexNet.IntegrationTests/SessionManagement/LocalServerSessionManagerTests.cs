using NexNet.Invocation;
using NUnit.Framework;

namespace NexNet.IntegrationTests.SessionManagement;

[TestFixture]
internal class LocalServerSessionManagerTests
{
    private LocalServerSessionManager _manager = null!;

    [SetUp]
    public void Setup()
    {
        _manager = new LocalServerSessionManager();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _manager.ShutdownAsync();
    }

    [Test]
    public async Task InitializeAsync_Completes()
    {
        await _manager.InitializeAsync();

        // No exception = success
        Assert.Pass();
    }

    [Test]
    public void Sessions_IsNotNull()
    {
        Assert.That(_manager.Sessions, Is.Not.Null);
    }

    [Test]
    public void Groups_IsNotNull()
    {
        Assert.That(_manager.Groups, Is.Not.Null);
    }

    [Test]
    public void Router_IsNotNull()
    {
        Assert.That(_manager.Router, Is.Not.Null);
    }

    [Test]
    public async Task FullWorkflow_RegisterGroupInvokeUnregister()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };

        // Register sessions
        await _manager.Sessions.RegisterSessionAsync(session1);
        await _manager.Sessions.RegisterSessionAsync(session2);

        // Verify registration
        Assert.That(await _manager.Sessions.GetSessionCountAsync(), Is.EqualTo(2));

        // Add to group
        await _manager.Groups.AddToGroupAsync("players", session1);
        await _manager.Groups.AddToGroupAsync("players", session2);

        // Verify group membership
        Assert.That(await _manager.Groups.GetGroupSizeAsync("players"), Is.EqualTo(2));

        // Invoke on group
        var message = CreateTestMessage();
        await _manager.Router.InvokeGroupAsync(message, "players");

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));

        // Unregister session1
        await _manager.Groups.RemoveFromAllGroupsAsync(session1);
        await _manager.Sessions.UnregisterSessionAsync(session1);

        // Verify session1 is removed
        Assert.That(await _manager.Sessions.SessionExistsAsync(1), Is.False);
        Assert.That(await _manager.Groups.GetGroupSizeAsync("players"), Is.EqualTo(1));

        // Invoke again - only session2 should receive
        session1.SentMessages.Clear();
        session2.SentMessages.Clear();
        await _manager.Router.InvokeGroupAsync(message, "players");

        Assert.That(session1.SentMessages, Is.Empty);
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ShutdownAsync_ClearsAllState()
    {
        var session = new MockNexusSession { Id = 1 };
        await _manager.Sessions.RegisterSessionAsync(session);
        await _manager.Groups.AddToGroupAsync("group1", session);

        await _manager.ShutdownAsync();

        Assert.That(await _manager.Sessions.GetSessionCountAsync(), Is.EqualTo(0));
        Assert.That(await _manager.Groups.GetGroupSizeAsync("group1"), Is.EqualTo(0));
    }

    [Test]
    public async Task MultipleGroupsAndSessions_CorrectRouting()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        var session3 = new MockNexusSession { Id = 3 };

        await _manager.Sessions.RegisterSessionAsync(session1);
        await _manager.Sessions.RegisterSessionAsync(session2);
        await _manager.Sessions.RegisterSessionAsync(session3);

        // session1 in admins and players
        await _manager.Groups.AddToGroupAsync("admins", session1);
        await _manager.Groups.AddToGroupAsync("players", session1);
        // session2 in players only
        await _manager.Groups.AddToGroupAsync("players", session2);
        // session3 in admins only
        await _manager.Groups.AddToGroupAsync("admins", session3);

        // Invoke on players
        var message = CreateTestMessage();
        await _manager.Router.InvokeGroupAsync(message, "players");

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session3.SentMessages, Is.Empty);

        // Clear and invoke on admins
        session1.SentMessages.Clear();
        session2.SentMessages.Clear();
        session3.SentMessages.Clear();

        await _manager.Router.InvokeGroupAsync(message, "admins");

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Is.Empty);
        Assert.That(session3.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InvokeAll_SendsToAllRegisteredSessions()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        var session3 = new MockNexusSession { Id = 3 };

        await _manager.Sessions.RegisterSessionAsync(session1);
        await _manager.Sessions.RegisterSessionAsync(session2);
        await _manager.Sessions.RegisterSessionAsync(session3);

        var message = CreateTestMessage();
        await _manager.Router.InvokeAllAsync(message);

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session3.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InvokeClients_SendsToSpecificSessions()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        var session3 = new MockNexusSession { Id = 3 };

        await _manager.Sessions.RegisterSessionAsync(session1);
        await _manager.Sessions.RegisterSessionAsync(session2);
        await _manager.Sessions.RegisterSessionAsync(session3);

        var message = CreateTestMessage();
        await _manager.Router.InvokeClientsAsync(message, [1L, 3L]);

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Is.Empty);
        Assert.That(session3.SentMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetSessionIds_ReturnsAllRegisteredIds()
    {
        await _manager.Sessions.RegisterSessionAsync(new MockNexusSession { Id = 100 });
        await _manager.Sessions.RegisterSessionAsync(new MockNexusSession { Id = 200 });
        await _manager.Sessions.RegisterSessionAsync(new MockNexusSession { Id = 300 });

        var ids = await _manager.Sessions.GetSessionIdsAsync();

        Assert.That(ids, Has.Count.EqualTo(3));
        Assert.That(ids, Contains.Item(100L));
        Assert.That(ids, Contains.Item(200L));
        Assert.That(ids, Contains.Item(300L));
    }

    [Test]
    public async Task GetGroupNames_ReturnsAllGroupNames()
    {
        var session = new MockNexusSession { Id = 1 };
        await _manager.Sessions.RegisterSessionAsync(session);

        await _manager.Groups.AddToGroupAsync("alpha", session);
        await _manager.Groups.AddToGroupAsync("beta", session);
        await _manager.Groups.AddToGroupAsync("gamma", session);

        var names = await _manager.Groups.GetGroupNamesAsync();

        Assert.That(names, Has.Count.EqualTo(3));
        Assert.That(names, Contains.Item("alpha"));
        Assert.That(names, Contains.Item("beta"));
        Assert.That(names, Contains.Item("gamma"));
    }

    [Test]
    public async Task IServerSessionManager_InterfaceAccess_Works()
    {
        IServerSessionManager manager = _manager;

        var session = new MockNexusSession { Id = 1 };
        await manager.Sessions.RegisterSessionAsync(session);
        await manager.Groups.AddToGroupAsync("test", session);

        Assert.That(await manager.Sessions.GetSessionCountAsync(), Is.EqualTo(1));
        Assert.That(await manager.Groups.GetGroupSizeAsync("test"), Is.EqualTo(1));
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
