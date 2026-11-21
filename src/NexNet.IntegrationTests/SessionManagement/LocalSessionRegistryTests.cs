using NexNet.Invocation;

namespace NexNet.IntegrationTests.SessionManagement;
internal class LocalSessionRegistryTests
{
    private LocalSessionContext _context = null!;
    private LocalSessionRegistry _registry = null!;

    [Before(HookType.Test)]
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

        await Assert.That(result).IsTrue();
        await Assert.That(_context.Sessions.ContainsKey(1)).IsTrue();
    }

    [Test]
    public async Task RegisterSessionAsync_DuplicateSession_ReturnsFalse()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.RegisterSessionAsync(session);

        var result = await _registry.RegisterSessionAsync(session);

        await Assert.That(result).IsFalse();
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

        await Assert.That(_context.Sessions.Count).IsEqualTo(3);
    }

    [Test]
    public async Task UnregisterSessionAsync_ExistingSession_RemovesFromContext()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.RegisterSessionAsync(session);

        await _registry.UnregisterSessionAsync(session);

        await Assert.That(_context.Sessions.ContainsKey(1)).IsFalse();
    }

    [Test]
    public async Task UnregisterSessionAsync_NonExistentSession_DoesNotThrow()
    {
        var session = new MockNexusSession { Id = 999 };

        await _registry.UnregisterSessionAsync(session);

        // Should not throw
    }

    [Test]
    public async Task GetSessionAsync_ExistingSession_ReturnsSession()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.RegisterSessionAsync(session);

        var result = await _registry.GetSessionAsync(1);

        await Assert.That(result).IsSameReferenceAs(session);
    }

    [Test]
    public async Task GetSessionAsync_NonExistentSession_ReturnsNull()
    {
        var result = await _registry.GetSessionAsync(999);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task SessionExistsAsync_ExistingSession_ReturnsTrue()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.RegisterSessionAsync(session);

        var result = await _registry.SessionExistsAsync(1);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task SessionExistsAsync_NonExistentSession_ReturnsFalse()
    {
        var result = await _registry.SessionExistsAsync(999);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task LocalSessions_ReturnsAllRegisteredSessions()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        await _registry.RegisterSessionAsync(session1);
        await _registry.RegisterSessionAsync(session2);

        var sessions = _registry.LocalSessions.ToList();

        await Assert.That(sessions).Count().IsEqualTo(2);
        await Assert.That(sessions).Contains(session1);
        await Assert.That(sessions).Contains(session2);
    }

    [Test]
    public async Task LocalSessions_EmptyRegistry_ReturnsEmpty()
    {
        var sessions = _registry.LocalSessions.ToList();

        await Assert.That(sessions).IsEmpty();
    }

    [Test]
    public async Task GetSessionCountAsync_ReturnsCorrectCount()
    {
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 1 });
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 2 });

        var count = await _registry.GetSessionCountAsync();

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task GetSessionCountAsync_EmptyRegistry_ReturnsZero()
    {
        var count = await _registry.GetSessionCountAsync();

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task GetSessionIdsAsync_ReturnsAllIds()
    {
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 1 });
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 2 });
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 3 });

        var ids = await _registry.GetSessionIdsAsync();

        await Assert.That(ids).Count().IsEqualTo(3);
        await Assert.That(ids).Contains(1L);
        await Assert.That(ids).Contains(2L);
        await Assert.That(ids).Contains(3L);
    }

    [Test]
    public async Task InitializeAsync_Completes()
    {
        await _registry.InitializeAsync();

        // No exception = success
    }

    [Test]
    public async Task ShutdownAsync_ClearsSessions()
    {
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 1 });
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 2 });

        await _registry.ShutdownAsync();

        await Assert.That(_context.Sessions).IsEmpty();
    }
}