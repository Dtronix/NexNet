using NexNet.Invocation;
using NUnit.Framework;

namespace NexNet.IntegrationTests.SessionManagement;

[TestFixture]
internal class LocalGroupRegistryTests
{
    private LocalSessionContext _context = null!;
    private LocalGroupRegistry _registry = null!;

    [SetUp]
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

        Assert.That(_context.GroupIdDictionary.ContainsKey("group1"), Is.True);
        Assert.That(session.RegisteredGroups, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AddToGroupAsync_ExistingGroup_AddsSessionToGroup()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        await _registry.AddToGroupAsync("group1", session1);

        await _registry.AddToGroupAsync("group1", session2);

        var members = _registry.GetLocalGroupMembers("group1").ToList();
        Assert.That(members, Has.Count.EqualTo(2));
        Assert.That(members, Contains.Item(session1));
        Assert.That(members, Contains.Item(session2));
    }

    [Test]
    public async Task AddToGroupAsync_SameSessionTwice_SessionOnlyAddedOnce()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);

        await _registry.AddToGroupAsync("group1", session);

        var members = _registry.GetLocalGroupMembers("group1").ToList();
        Assert.That(members, Has.Count.EqualTo(1));
        // RegisteredGroups list may have duplicates in this impl, but group itself won't
    }

    [Test]
    public async Task AddToGroupsAsync_MultipleGroups_AddsSessionToAll()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.AddToGroupsAsync(["group1", "group2", "group3"], session);

        Assert.That(session.RegisteredGroups, Has.Count.EqualTo(3));
        Assert.That(_registry.GetLocalGroupMembers("group1"), Contains.Item(session));
        Assert.That(_registry.GetLocalGroupMembers("group2"), Contains.Item(session));
        Assert.That(_registry.GetLocalGroupMembers("group3"), Contains.Item(session));
    }

    [Test]
    public async Task AddToGroupsAsync_EmptyArray_DoesNothing()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.AddToGroupsAsync([], session);

        Assert.That(session.RegisteredGroups, Is.Empty);
    }

    [Test]
    public async Task RemoveFromGroupAsync_ExistingMember_RemovesFromGroup()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);

        await _registry.RemoveFromGroupAsync("group1", session);

        var members = _registry.GetLocalGroupMembers("group1").ToList();
        Assert.That(members, Does.Not.Contain(session));
    }

    [Test]
    public async Task RemoveFromGroupAsync_NonExistentGroup_DoesNotThrow()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.RemoveFromGroupAsync("nonexistent", session);

        // Should not throw
        Assert.Pass();
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
        Assert.That(members, Contains.Item(session1));
    }

    [Test]
    public async Task RemoveFromAllGroupsAsync_SessionInMultipleGroups_RemovesFromAll()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupsAsync(["group1", "group2"], session);

        await _registry.RemoveFromAllGroupsAsync(session);

        Assert.That(session.RegisteredGroups, Is.Empty);
        Assert.That(_registry.GetLocalGroupMembers("group1"), Does.Not.Contain(session));
        Assert.That(_registry.GetLocalGroupMembers("group2"), Does.Not.Contain(session));
    }

    [Test]
    public async Task RemoveFromAllGroupsAsync_SessionNotInAnyGroup_DoesNotThrow()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.RemoveFromAllGroupsAsync(session);

        // Should not throw
        Assert.Pass();
    }

    [Test]
    public async Task GetGroupNamesAsync_ReturnsAllGroupNames()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);
        await _registry.AddToGroupAsync("group2", session);

        var names = await _registry.GetGroupNamesAsync();

        Assert.That(names, Has.Count.EqualTo(2));
        Assert.That(names, Contains.Item("group1"));
        Assert.That(names, Contains.Item("group2"));
    }

    [Test]
    public async Task GetGroupNamesAsync_NoGroups_ReturnsEmpty()
    {
        var names = await _registry.GetGroupNamesAsync();

        Assert.That(names, Is.Empty);
    }

    [Test]
    public async Task GetGroupSizeAsync_ReturnsCorrectCount()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        await _registry.AddToGroupAsync("group1", session1);
        await _registry.AddToGroupAsync("group1", session2);

        var size = await _registry.GetGroupSizeAsync("group1");

        Assert.That(size, Is.EqualTo(2));
    }

    [Test]
    public async Task GetGroupSizeAsync_NonExistentGroup_ReturnsZero()
    {
        var size = await _registry.GetGroupSizeAsync("nonexistent");

        Assert.That(size, Is.EqualTo(0));
    }

    [Test]
    public async Task GetGroupSizeAsync_EmptyGroup_ReturnsZero()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);
        await _registry.RemoveFromGroupAsync("group1", session);

        var size = await _registry.GetGroupSizeAsync("group1");

        Assert.That(size, Is.EqualTo(0));
    }

    [Test]
    public async Task GetLocalGroupMembers_ReturnsGroupMembers()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        await _registry.AddToGroupAsync("group1", session1);
        await _registry.AddToGroupAsync("group1", session2);

        var members = _registry.GetLocalGroupMembers("group1").ToList();

        Assert.That(members, Has.Count.EqualTo(2));
        Assert.That(members, Contains.Item(session1));
        Assert.That(members, Contains.Item(session2));
    }

    [Test]
    public void GetLocalGroupMembers_NonExistentGroup_ReturnsEmpty()
    {
        var members = _registry.GetLocalGroupMembers("nonexistent").ToList();

        Assert.That(members, Is.Empty);
    }

    [Test]
    public async Task InitializeAsync_Completes()
    {
        await _registry.InitializeAsync();

        // No exception = success
        Assert.Pass();
    }

    [Test]
    public async Task ShutdownAsync_ClearsAllGroups()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);
        await _registry.AddToGroupAsync("group2", session);

        await _registry.ShutdownAsync();

        Assert.That(_context.SessionGroups, Is.Empty);
        Assert.That(_context.GroupIdDictionary, Is.Empty);
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

        Assert.That(players, Has.Count.EqualTo(2));
        Assert.That(players, Contains.Item(session1));
        Assert.That(players, Contains.Item(session2));

        Assert.That(admins, Has.Count.EqualTo(2));
        Assert.That(admins, Contains.Item(session1));
        Assert.That(admins, Contains.Item(session3));
    }
}
