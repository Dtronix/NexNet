using NexNet.Invocation;
using NUnit.Framework;

namespace NexNet.IntegrationTests.SessionManagement;

[TestFixture]
internal class LocalSessionRegistryTests
{
    private LocalSessionContext _context = null!;
    private LocalSessionRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        _context = new LocalSessionContext();
        _registry = new LocalSessionRegistry(_context);
    }

    [Test]
    public async Task RegisterSessionAsync_NewSession_ReturnsTrue()
    {
        var session = new MockNexusSession { Id = 1 };

        var result = await _registry.RegisterSessionAsync(session);

        Assert.That(result, Is.True);
        Assert.That(_context.Sessions.ContainsKey(1), Is.True);
    }

    [Test]
    public async Task RegisterSessionAsync_DuplicateSession_ReturnsFalse()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.RegisterSessionAsync(session);

        var result = await _registry.RegisterSessionAsync(session);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task RegisterSessionAsync_MultipleSessions_AllRegistered()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        var session3 = new MockNexusSession { Id = 3 };

        await _registry.RegisterSessionAsync(session1);
        await _registry.RegisterSessionAsync(session2);
        await _registry.RegisterSessionAsync(session3);

        Assert.That(_context.Sessions.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task UnregisterSessionAsync_ExistingSession_RemovesFromContext()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.RegisterSessionAsync(session);

        await _registry.UnregisterSessionAsync(session);

        Assert.That(_context.Sessions.ContainsKey(1), Is.False);
    }

    [Test]
    public async Task UnregisterSessionAsync_NonExistentSession_DoesNotThrow()
    {
        var session = new MockNexusSession { Id = 999 };

        await _registry.UnregisterSessionAsync(session);

        // Should not throw
        Assert.Pass();
    }

    [Test]
    public async Task GetSessionAsync_ExistingSession_ReturnsSession()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.RegisterSessionAsync(session);

        var result = await _registry.GetSessionAsync(1);

        Assert.That(result, Is.SameAs(session));
    }

    [Test]
    public async Task GetSessionAsync_NonExistentSession_ReturnsNull()
    {
        var result = await _registry.GetSessionAsync(999);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SessionExistsAsync_ExistingSession_ReturnsTrue()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.RegisterSessionAsync(session);

        var result = await _registry.SessionExistsAsync(1);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task SessionExistsAsync_NonExistentSession_ReturnsFalse()
    {
        var result = await _registry.SessionExistsAsync(999);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task LocalSessions_ReturnsAllRegisteredSessions()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        await _registry.RegisterSessionAsync(session1);
        await _registry.RegisterSessionAsync(session2);

        var sessions = _registry.LocalSessions.ToList();

        Assert.That(sessions, Has.Count.EqualTo(2));
        Assert.That(sessions, Contains.Item(session1));
        Assert.That(sessions, Contains.Item(session2));
    }

    [Test]
    public void LocalSessions_EmptyRegistry_ReturnsEmpty()
    {
        var sessions = _registry.LocalSessions.ToList();

        Assert.That(sessions, Is.Empty);
    }

    [Test]
    public async Task GetSessionCountAsync_ReturnsCorrectCount()
    {
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 1 });
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 2 });

        var count = await _registry.GetSessionCountAsync();

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetSessionCountAsync_EmptyRegistry_ReturnsZero()
    {
        var count = await _registry.GetSessionCountAsync();

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetSessionIdsAsync_ReturnsAllIds()
    {
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 1 });
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 2 });
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 3 });

        var ids = await _registry.GetSessionIdsAsync();

        Assert.That(ids, Has.Count.EqualTo(3));
        Assert.That(ids, Contains.Item(1L));
        Assert.That(ids, Contains.Item(2L));
        Assert.That(ids, Contains.Item(3L));
    }

    [Test]
    public async Task InitializeAsync_Completes()
    {
        await _registry.InitializeAsync();

        // No exception = success
        Assert.Pass();
    }

    [Test]
    public async Task ShutdownAsync_ClearsSessions()
    {
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 1 });
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 2 });

        await _registry.ShutdownAsync();

        Assert.That(_context.Sessions, Is.Empty);
    }
}
