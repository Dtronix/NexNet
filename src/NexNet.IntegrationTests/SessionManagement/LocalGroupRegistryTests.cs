using NexNet.Invocation;

namespace NexNet.IntegrationTests.SessionManagement;
internal class LocalGroupRegistryTests
{
    private LocalSessionContext _context = null!;
    private LocalGroupRegistry _registry = null!;

    [Before(HookType.Test)]
    public void Setup()
    {
        _context = new LocalSessionContext();
        _registry = new LocalGroupRegistry(_context);
    }

    [Test]
    public async Task AddToGroupAsync_NewGroup_CreatesGroupAndAddsSession()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.AddToGroupAsync("group1", session);

        await Assert.That(_context.GroupIdDictionary.ContainsKey("group1")).IsTrue();
        await Assert.That(session.RegisteredGroups).Count().IsEqualTo(1);
    }

    [Test]
    public async Task AddToGroupAsync_ExistingGroup_AddsSessionToGroup()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        await _registry.AddToGroupAsync("group1", session1);

        await _registry.AddToGroupAsync("group1", session2);

        var members = _registry.GetLocalGroupMembers("group1").ToList();
        await Assert.That(members).Count().IsEqualTo(2);
        await Assert.That(members).Contains(session1);
        await Assert.That(members).Contains(session2);
    }

    [Test]
    public async Task AddToGroupAsync_SameSessionTwice_SessionOnlyAddedOnce()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);

        await _registry.AddToGroupAsync("group1", session);

        var members = _registry.GetLocalGroupMembers("group1").ToList();
        await Assert.That(members).Count().IsEqualTo(1);
        // RegisteredGroups list may have duplicates in this impl, but group itself won't
    }

    [Test]
    public async Task AddToGroupsAsync_MultipleGroups_AddsSessionToAll()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.AddToGroupsAsync(["group1", "group2", "group3"], session);

        await Assert.That(session.RegisteredGroups).Count().IsEqualTo(3);
        await Assert.That(_registry.GetLocalGroupMembers("group1")).Contains(session);
        await Assert.That(_registry.GetLocalGroupMembers("group2")).Contains(session);
        await Assert.That(_registry.GetLocalGroupMembers("group3")).Contains(session);
    }

    [Test]
    public async Task AddToGroupsAsync_EmptyArray_DoesNothing()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.AddToGroupsAsync([], session);

        await Assert.That(session.RegisteredGroups).IsEmpty();
    }

    [Test]
    public async Task RemoveFromGroupAsync_ExistingMember_RemovesFromGroup()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);

        await _registry.RemoveFromGroupAsync("group1", session);

        var members = _registry.GetLocalGroupMembers("group1").ToList();
        await Assert.That(members).DoesNotContain(session);
    }

    [Test]
    public async Task RemoveFromGroupAsync_NonExistentGroup_DoesNotThrow()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.RemoveFromGroupAsync("nonexistent", session);

        // Should not throw
    }

    [Test]
    public async Task RemoveFromGroupAsync_SessionNotInGroup_DoesNotThrow()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        await _registry.AddToGroupAsync("group1", session1);

        await _registry.RemoveFromGroupAsync("group1", session2);

        // Should not throw, session1 should still be in group
        var members = _registry.GetLocalGroupMembers("group1").ToList();
        await Assert.That(members).Contains(session1);
    }

    [Test]
    public async Task RemoveFromAllGroupsAsync_SessionInMultipleGroups_RemovesFromAll()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupsAsync(["group1", "group2"], session);

        await _registry.RemoveFromAllGroupsAsync(session);

        await Assert.That(session.RegisteredGroups).IsEmpty();
        await Assert.That(_registry.GetLocalGroupMembers("group1")).DoesNotContain(session);
        await Assert.That(_registry.GetLocalGroupMembers("group2")).DoesNotContain(session);
    }

    [Test]
    public async Task RemoveFromAllGroupsAsync_SessionNotInAnyGroup_DoesNotThrow()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.RemoveFromAllGroupsAsync(session);

        // Should not throw
    }

    [Test]
    public async Task GetGroupNamesAsync_ReturnsAllGroupNames()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);
        await _registry.AddToGroupAsync("group2", session);

        var names = await _registry.GetGroupNamesAsync();

        await Assert.That(names).Count().IsEqualTo(2);
        await Assert.That(names).Contains("group1");
        await Assert.That(names).Contains("group2");
    }

    [Test]
    public async Task GetGroupNamesAsync_NoGroups_ReturnsEmpty()
    {
        var names = await _registry.GetGroupNamesAsync();

        await Assert.That(names).IsEmpty();
    }

    [Test]
    public async Task GetGroupSizeAsync_ReturnsCorrectCount()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        await _registry.AddToGroupAsync("group1", session1);
        await _registry.AddToGroupAsync("group1", session2);

        var size = await _registry.GetGroupSizeAsync("group1");

        await Assert.That(size).IsEqualTo(2);
    }

    [Test]
    public async Task GetGroupSizeAsync_NonExistentGroup_ReturnsZero()
    {
        var size = await _registry.GetGroupSizeAsync("nonexistent");

        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task GetGroupSizeAsync_EmptyGroup_ReturnsZero()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);
        await _registry.RemoveFromGroupAsync("group1", session);

        var size = await _registry.GetGroupSizeAsync("group1");

        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task GetLocalGroupMembers_ReturnsGroupMembers()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        await _registry.AddToGroupAsync("group1", session1);
        await _registry.AddToGroupAsync("group1", session2);

        var members = _registry.GetLocalGroupMembers("group1").ToList();

        await Assert.That(members).Count().IsEqualTo(2);
        await Assert.That(members).Contains(session1);
        await Assert.That(members).Contains(session2);
    }

    [Test]
    public async Task GetLocalGroupMembers_NonExistentGroup_ReturnsEmpty()
    {
        var members = _registry.GetLocalGroupMembers("nonexistent").ToList();

        await Assert.That(members).IsEmpty();
    }

    [Test]
    public async Task InitializeAsync_Completes()
    {
        await _registry.InitializeAsync();

        // No exception = success
    }

    [Test]
    public async Task ShutdownAsync_ClearsAllGroups()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);
        await _registry.AddToGroupAsync("group2", session);

        await _registry.ShutdownAsync();

        await Assert.That(_context.SessionGroups).IsEmpty();
        await Assert.That(_context.GroupIdDictionary).IsEmpty();
    }

    [Test]
    public async Task MultipleSessionsInMultipleGroups_CorrectMembership()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        var session3 = new MockNexusSession { Id = 3 };

        await _registry.AddToGroupAsync("players", session1);
        await _registry.AddToGroupAsync("players", session2);
        await _registry.AddToGroupAsync("admins", session1);
        await _registry.AddToGroupAsync("admins", session3);

        var players = _registry.GetLocalGroupMembers("players").ToList();
        var admins = _registry.GetLocalGroupMembers("admins").ToList();

        await Assert.That(players).Count().IsEqualTo(2);
        await Assert.That(players).Contains(session1);
        await Assert.That(players).Contains(session2);

        await Assert.That(admins).Count().IsEqualTo(2);
        await Assert.That(admins).Contains(session1);
        await Assert.That(admins).Contains(session3);
    }
}