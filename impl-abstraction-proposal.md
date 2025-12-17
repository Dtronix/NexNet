# Session, Group, and Invocation Abstraction

## Goal

Extract session management, group management, and invocation routing from tightly-coupled implementations into abstracted interfaces. All methods must return `ValueTask` or `ValueTask<T>` to support future async backplane implementations.

**Current state**: Logic is embedded in `SessionManager`, `GroupManager`, and `ProxyInvocationBase`.

**Target state**:
- Three **public** interfaces: `ISessionRegistry`, `IGroupRegistry`, `IInvocationRouter`
- One **public** generic composite class: `ServerSessionManager<TSessionRegistry, TGroupRegistry, TInvocationRouter>`
- Three **internal** local implementations: `LocalSessionRegistry`, `LocalGroupRegistry`, `LocalInvocationRouter`
- Shared state via **internal** `LocalSessionContext` class

## Design Decisions

| Decision | Choice |
|----------|--------|
| Interface visibility | **Public** - `ISessionRegistry`, `IGroupRegistry`, `IInvocationRouter`, `IServerSessionManager` |
| Abstract base class | **Public** - `ServerSessionManager<T1,T2,T3>` allows custom implementations |
| Local implementation visibility | **Internal** - all `Local*` classes hidden from users |
| File layout | **Separate files** - one interface per file |
| Default behavior | **Null = default** - if `ServerConfig.SessionManager` is null, `LocalServerSessionManager` is created internally |
| Shared state | **Shared context** - internal `LocalSessionContext` holds shared dictionaries |
| Sync methods | **Removed entirely** - no backward compatibility period |
| Invocation ID assignment | **Internal to router** - not passed as parameter |
| Initialization | **Separate `InitializeAsync()`** - for async setup |

---

## 1. Interface Definitions

### 1.1 ISessionRegistry

Manages session registration and lookup.

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Registry for managing session lifecycle across the server infrastructure.
/// </summary>
public interface ISessionRegistry
{
    /// <summary>
    /// Registers a session with the registry.
    /// </summary>
    /// <param name="session">Session to register.</param>
    /// <returns>True if registration succeeded, false if session already exists.</returns>
    ValueTask<bool> RegisterSessionAsync(INexusSession session);

    /// <summary>
    /// Unregisters a session from the registry.
    /// </summary>
    /// <param name="session">Session to unregister.</param>
    ValueTask UnregisterSessionAsync(INexusSession session);

    /// <summary>
    /// Attempts to get a session by ID.
    /// </summary>
    /// <param name="sessionId">The session ID to look up.</param>
    /// <returns>The session if found, null otherwise.</returns>
    ValueTask<INexusSession?> GetSessionAsync(long sessionId);

    /// <summary>
    /// Checks if a session exists locally on this server.
    /// </summary>
    /// <param name="sessionId">The session ID to check.</param>
    /// <returns>True if session exists locally.</returns>
    ValueTask<bool> SessionExistsAsync(long sessionId);

    /// <summary>
    /// Gets all locally connected sessions.
    /// For local implementation, returns all sessions.
    /// For backplane implementation, returns only sessions on this server.
    /// </summary>
    IEnumerable<INexusSession> LocalSessions { get; }

    /// <summary>
    /// Gets the count of all sessions (local only for local impl, global for backplane).
    /// </summary>
    ValueTask<long> GetSessionCountAsync();

    /// <summary>
    /// Gets all session IDs (local only for local impl).
    /// </summary>
    ValueTask<IReadOnlyCollection<long>> GetSessionIdsAsync();

    /// <summary>
    /// Initializes the registry. Called when the server starts.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the registry. Called when the server stops.
    /// </summary>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
```

### 1.2 IGroupRegistry

Manages group membership for sessions.

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Registry for managing group membership across sessions.
/// </summary>
public interface IGroupRegistry
{
    /// <summary>
    /// Adds a session to a group.
    /// </summary>
    /// <param name="groupName">Name of the group.</param>
    /// <param name="session">Session to add.</param>
    ValueTask AddToGroupAsync(string groupName, INexusSession session);

    /// <summary>
    /// Adds a session to multiple groups.
    /// </summary>
    /// <param name="groupNames">Names of the groups.</param>
    /// <param name="session">Session to add.</param>
    ValueTask AddToGroupsAsync(string[] groupNames, INexusSession session);

    /// <summary>
    /// Removes a session from a group.
    /// </summary>
    /// <param name="groupName">Name of the group.</param>
    /// <param name="session">Session to remove.</param>
    ValueTask RemoveFromGroupAsync(string groupName, INexusSession session);

    /// <summary>
    /// Removes a session from all groups it belongs to.
    /// Called during session disconnection.
    /// </summary>
    /// <param name="session">Session to remove from all groups.</param>
    ValueTask RemoveFromAllGroupsAsync(INexusSession session);

    /// <summary>
    /// Gets all group names that have at least one member.
    /// </summary>
    ValueTask<IReadOnlyCollection<string>> GetGroupNamesAsync();

    /// <summary>
    /// Gets the count of sessions in a group (local only for local impl).
    /// </summary>
    /// <param name="groupName">Name of the group.</param>
    ValueTask<long> GetGroupSizeAsync(string groupName);

    /// <summary>
    /// Gets sessions that are members of a group (local sessions only).
    /// </summary>
    /// <param name="groupName">Name of the group.</param>
    IEnumerable<INexusSession> GetLocalGroupMembers(string groupName);

    /// <summary>
    /// Initializes the registry. Called when the server starts.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the registry. Called when the server stops.
    /// </summary>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
```

### 1.3 IInvocationRouter

Routes invocations to appropriate sessions. This is the key interface that handles message delivery.

**Note**: Invocation ID assignment is handled internally by the implementation, not passed as a parameter.

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Routes invocation messages to target sessions.
/// Handles all invocation modes: All, Others, Client, Clients, Group, etc.
/// Invocation ID assignment is handled internally per-session.
/// </summary>
public interface IInvocationRouter
{
    /// <summary>
    /// Invokes on all connected sessions.
    /// </summary>
    /// <param name="message">The message to send.</param>
    ValueTask InvokeAllAsync<TMessage>(TMessage message)
        where TMessage : IInvocationMessage;

    /// <summary>
    /// Invokes on all connected sessions except the specified one.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="excludeSessionId">Session ID to exclude.</param>
    ValueTask InvokeAllExceptAsync<TMessage>(TMessage message, long excludeSessionId)
        where TMessage : IInvocationMessage;

    /// <summary>
    /// Invokes on a specific session by ID.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="sessionId">Target session ID.</param>
    /// <returns>True if session was found and message sent, false otherwise.</returns>
    ValueTask<bool> InvokeClientAsync<TMessage>(TMessage message, long sessionId)
        where TMessage : IInvocationMessage;

    /// <summary>
    /// Invokes on multiple specific sessions by ID.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="sessionIds">Target session IDs.</param>
    ValueTask InvokeClientsAsync<TMessage>(TMessage message, long[] sessionIds)
        where TMessage : IInvocationMessage;

    /// <summary>
    /// Invokes on all sessions in a group.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="groupName">Target group name.</param>
    /// <param name="excludeSessionId">Optional session ID to exclude (for GroupExceptCaller).</param>
    ValueTask InvokeGroupAsync<TMessage>(TMessage message, string groupName, long? excludeSessionId = null)
        where TMessage : IInvocationMessage;

    /// <summary>
    /// Invokes on all sessions in multiple groups.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="groupNames">Target group names.</param>
    /// <param name="excludeSessionId">Optional session ID to exclude.</param>
    ValueTask InvokeGroupsAsync<TMessage>(TMessage message, string[] groupNames, long? excludeSessionId = null)
        where TMessage : IInvocationMessage;

    /// <summary>
    /// Initializes the router. Called when the server starts.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the router. Called when the server stops.
    /// </summary>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
```

---

## 2. Abstract Generic Base Class

An abstract generic base class that holds the three implementations with type constraints. Concrete implementations extend this class.

### 2.1 Non-Generic Base Interface

For use in `ServerConfig` where we don't want to expose generic type parameters:

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Non-generic base interface for ServerSessionManager.
/// Used by ServerConfig to hold any ServerSessionManager variant.
/// </summary>
public interface IServerSessionManager
{
    /// <summary>
    /// The session registry.
    /// </summary>
    ISessionRegistry Sessions { get; }

    /// <summary>
    /// The group registry.
    /// </summary>
    IGroupRegistry Groups { get; }

    /// <summary>
    /// The invocation router.
    /// </summary>
    IInvocationRouter Router { get; }

    /// <summary>
    /// Initializes all components.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down all components.
    /// </summary>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
```

### 2.2 Abstract Generic Base Class

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Abstract base class for session managers that combines session registry, group registry, and invocation routing.
/// Extend this class to create custom implementations (e.g., backplane-backed).
/// </summary>
/// <typeparam name="TSessionRegistry">Session registry implementation.</typeparam>
/// <typeparam name="TGroupRegistry">Group registry implementation.</typeparam>
/// <typeparam name="TInvocationRouter">Invocation router implementation.</typeparam>
public abstract class ServerSessionManager<TSessionRegistry, TGroupRegistry, TInvocationRouter>
    : IServerSessionManager
    where TSessionRegistry : ISessionRegistry
    where TGroupRegistry : IGroupRegistry
    where TInvocationRouter : IInvocationRouter
{
    /// <summary>
    /// The session registry implementation.
    /// </summary>
    public TSessionRegistry Sessions { get; }

    /// <summary>
    /// The group registry implementation.
    /// </summary>
    public TGroupRegistry Groups { get; }

    /// <summary>
    /// The invocation router implementation.
    /// </summary>
    public TInvocationRouter Router { get; }

    // Explicit interface implementation for non-generic access
    ISessionRegistry IServerSessionManager.Sessions => Sessions;
    IGroupRegistry IServerSessionManager.Groups => Groups;
    IInvocationRouter IServerSessionManager.Router => Router;

    /// <summary>
    /// Creates a new ServerSessionManager with the specified implementations.
    /// </summary>
    protected ServerSessionManager(
        TSessionRegistry sessionRegistry,
        TGroupRegistry groupRegistry,
        TInvocationRouter invocationRouter)
    {
        Sessions = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        Groups = groupRegistry ?? throw new ArgumentNullException(nameof(groupRegistry));
        Router = invocationRouter ?? throw new ArgumentNullException(nameof(invocationRouter));
    }

    /// <summary>
    /// Initializes all components. Called when the server starts.
    /// Override to add custom initialization logic.
    /// </summary>
    public virtual async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Sessions.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Groups.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Router.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shuts down all components. Called when the server stops.
    /// Override to add custom shutdown logic.
    /// </summary>
    public virtual async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await Router.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        await Groups.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        await Sessions.ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

### 2.3 Default Local Implementation (Internal)

The default implementation is **internal**. It is only instantiated by `NexusServer` when `ServerConfig.SessionManager` is null.

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Default local (single-server) implementation of ServerSessionManager.
/// Internal - only instantiated by NexusServer when SessionManager is null.
/// </summary>
internal sealed class LocalServerSessionManager
    : ServerSessionManager<LocalSessionRegistry, LocalGroupRegistry, LocalInvocationRouter>
{
    private readonly LocalSessionContext _context;

    /// <summary>
    /// Creates a new LocalServerSessionManager with a fresh context.
    /// </summary>
    public LocalServerSessionManager()
        : this(new LocalSessionContext())
    {
    }

    private LocalServerSessionManager(LocalSessionContext context)
        : base(
            new LocalSessionRegistry(context),
            new LocalGroupRegistry(context),
            new LocalInvocationRouter(context))
    {
        _context = context;
    }

    /// <summary>
    /// Shuts down and clears all state.
    /// </summary>
    public override async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await base.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        _context.Clear();
    }
}
```

### Usage Examples

```csharp
// Default usage - local single-server (most common)
// SessionManager is null, so LocalServerSessionManager is created internally
var config = new TcpServerConfig(...);

// Custom backplane implementation
var config = new TcpServerConfig(...)
{
    SessionManager = new GarnetServerSessionManager(connectionString)
};

// Custom implementation by user
public class MyCustomSessionManager
    : ServerSessionManager<MySessionRegistry, MyGroupRegistry, MyInvocationRouter>
{
    public MyCustomSessionManager()
        : base(new MySessionRegistry(), new MyGroupRegistry(), new MyInvocationRouter())
    {
    }
}

var config = new TcpServerConfig(...)
{
    SessionManager = new MyCustomSessionManager()
};
```

---

## 3. Local Implementation Components (All Internal)

The local implementation consists of **internal** classes sharing state via `LocalSessionContext`. These are only used when `ServerConfig.SessionManager` is null.

### 3.1 Shared Context

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Shared state for local session manager implementations.
/// </summary>
internal sealed class LocalSessionContext
{
    public readonly ConcurrentDictionary<long, INexusSession> Sessions = new();
    public readonly ConcurrentDictionary<string, int> GroupIdDictionary = new();
    public readonly ConcurrentDictionary<int, LocalSessionGroup> SessionGroups = new();

    private int _groupIdCounter = 0;

    public int GetNextGroupId() => Interlocked.Increment(ref _groupIdCounter);

    public void Clear()
    {
        Sessions.Clear();
        SessionGroups.Clear();
        GroupIdDictionary.Clear();
    }
}

/// <summary>
/// A group of sessions.
/// </summary>
internal sealed class LocalSessionGroup
{
    private readonly ConcurrentDictionary<long, INexusSession> _sessionDictionary = new();

    public int Count => _sessionDictionary.Count;
    public IEnumerable<INexusSession> Sessions => _sessionDictionary.Values;

    public bool RegisterSession(INexusSession session)
    {
        return _sessionDictionary.TryAdd(session.Id, session);
    }

    public void UnregisterSession(INexusSession session)
    {
        _sessionDictionary.TryRemove(session.Id, out _);
    }
}
```

### 3.2 LocalSessionRegistry

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Local (single-server) implementation of ISessionRegistry.
/// </summary>
internal sealed class LocalSessionRegistry : ISessionRegistry
{
    private readonly LocalSessionContext _context;

    public LocalSessionRegistry(LocalSessionContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ValueTask<bool> RegisterSessionAsync(INexusSession session)
    {
        var result = _context.Sessions.TryAdd(session.Id, session);
        return ValueTask.FromResult(result);
    }

    public ValueTask UnregisterSessionAsync(INexusSession session)
    {
        _context.Sessions.TryRemove(session.Id, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<INexusSession?> GetSessionAsync(long sessionId)
    {
        _context.Sessions.TryGetValue(sessionId, out var session);
        return ValueTask.FromResult(session);
    }

    public ValueTask<bool> SessionExistsAsync(long sessionId)
    {
        return ValueTask.FromResult(_context.Sessions.ContainsKey(sessionId));
    }

    public IEnumerable<INexusSession> LocalSessions => _context.Sessions.Values;

    public ValueTask<long> GetSessionCountAsync()
    {
        return ValueTask.FromResult((long)_context.Sessions.Count);
    }

    public ValueTask<IReadOnlyCollection<long>> GetSessionIdsAsync()
    {
        return ValueTask.FromResult<IReadOnlyCollection<long>>(_context.Sessions.Keys.ToArray());
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _context.Sessions.Clear();
        return ValueTask.CompletedTask;
    }
}
```

### 3.3 LocalGroupRegistry

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Local (single-server) implementation of IGroupRegistry.
/// </summary>
internal sealed class LocalGroupRegistry : IGroupRegistry
{
    private readonly LocalSessionContext _context;

    public LocalGroupRegistry(LocalSessionContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ValueTask AddToGroupAsync(string groupName, INexusSession session)
    {
        session.Logger?.LogDebug($"Registering session {session.Id} to group {groupName}");

        var id = _context.GroupIdDictionary.GetOrAdd(groupName, _ => _context.GetNextGroupId());
        var group = _context.SessionGroups.GetOrAdd(id, _ => new LocalSessionGroup());
        group.RegisterSession(session);

        lock (session.RegisteredGroupsLock)
        {
            session.RegisteredGroups.Add(id);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AddToGroupsAsync(string[] groupNames, INexusSession session)
    {
        session.Logger?.LogDebug($"Registering session {session.Id} to groups {string.Join(',', groupNames)}");

        int[] groupIds = new int[groupNames.Length];

        for (int i = 0; i < groupNames.Length; i++)
        {
            groupIds[i] = _context.GroupIdDictionary.GetOrAdd(groupNames[i], _ => _context.GetNextGroupId());
            var group = _context.SessionGroups.GetOrAdd(groupIds[i], _ => new LocalSessionGroup());
            group.RegisterSession(session);
        }

        lock (session.RegisteredGroupsLock)
        {
            session.RegisteredGroups.AddRange(groupIds);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveFromGroupAsync(string groupName, INexusSession session)
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return ValueTask.CompletedTask;

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return ValueTask.CompletedTask;

        session.Logger?.LogDebug($"Unregistering session {session.Id} from group {groupName}");
        group.UnregisterSession(session);

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveFromAllGroupsAsync(INexusSession session)
    {
        lock (session.RegisteredGroupsLock)
        {
            var groups = session.RegisteredGroups;
            for (var i = 0; i < groups.Count; i++)
            {
                if (_context.SessionGroups.TryGetValue(groups[i], out var group))
                {
                    group.UnregisterSession(session);
                }
            }
            groups.Clear();
            groups.TrimExcess();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyCollection<string>> GetGroupNamesAsync()
    {
        return ValueTask.FromResult<IReadOnlyCollection<string>>(_context.GroupIdDictionary.Keys.ToArray());
    }

    public ValueTask<long> GetGroupSizeAsync(string groupName)
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return ValueTask.FromResult(0L);

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return ValueTask.FromResult(0L);

        return ValueTask.FromResult((long)group.Count);
    }

    public IEnumerable<INexusSession> GetLocalGroupMembers(string groupName)
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return Enumerable.Empty<INexusSession>();

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return Enumerable.Empty<INexusSession>();

        return group.Sessions;
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _context.SessionGroups.Clear();
        _context.GroupIdDictionary.Clear();
        return ValueTask.CompletedTask;
    }
}
```

### 3.4 LocalInvocationRouter

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Local (single-server) implementation of IInvocationRouter.
/// </summary>
internal sealed class LocalInvocationRouter : IInvocationRouter
{
    private readonly LocalSessionContext _context;

    public LocalInvocationRouter(LocalSessionContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async ValueTask InvokeAllAsync<TMessage>(TMessage message)
        where TMessage : IInvocationMessage
    {
        foreach (var (_, session) in _context.Sessions)
        {
            message.InvocationId = session.SessionInvocationStateManager.GetNextId(false);

            try
            {
                await session.SendMessage(message).ConfigureAwait(false);
            }
            catch
            {
                // Fire-and-forget: ignore send failures
            }
        }
    }

    public async ValueTask InvokeAllExceptAsync<TMessage>(TMessage message, long excludeSessionId)
        where TMessage : IInvocationMessage
    {
        foreach (var (id, session) in _context.Sessions)
        {
            if (id == excludeSessionId)
                continue;

            try
            {
                await session.SendMessage(message).ConfigureAwait(false);
            }
            catch
            {
                // Fire-and-forget: ignore send failures
            }
        }
    }

    public async ValueTask<bool> InvokeClientAsync<TMessage>(TMessage message, long sessionId)
        where TMessage : IInvocationMessage
    {
        if (!_context.Sessions.TryGetValue(sessionId, out var session))
            return false;

        try
        {
            await session.SendMessage(message).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask InvokeClientsAsync<TMessage>(TMessage message, long[] sessionIds)
        where TMessage : IInvocationMessage
    {
        for (int i = 0; i < sessionIds.Length; i++)
        {
            if (_context.Sessions.TryGetValue(sessionIds[i], out var session))
            {
                try
                {
                    await session.SendMessage(message).ConfigureAwait(false);
                }
                catch
                {
                    // Fire-and-forget: ignore send failures
                }
            }
        }
    }

    public async ValueTask InvokeGroupAsync<TMessage>(TMessage message, string groupName, long? excludeSessionId = null)
        where TMessage : IInvocationMessage
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return;

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return;

        foreach (var session in group.Sessions)
        {
            if (excludeSessionId.HasValue && session.Id == excludeSessionId)
                continue;

            try
            {
                await session.SendMessage(message).ConfigureAwait(false);
            }
            catch
            {
                // Fire-and-forget: ignore send failures
            }
        }
    }

    public async ValueTask InvokeGroupsAsync<TMessage>(TMessage message, string[] groupNames, long? excludeSessionId = null)
        where TMessage : IInvocationMessage
    {
        for (int i = 0; i < groupNames.Length; i++)
        {
            await InvokeGroupAsync(message, groupNames[i], excludeSessionId).ConfigureAwait(false);
        }
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}
```


---

## 4. Testing Strategy

Since internals are visible to the test project, the local implementations can be tested directly.

### 4.1 Mock Session for Testing

```csharp
/// <summary>
/// Mock session for testing registry and router implementations.
/// </summary>
internal class MockNexusSession : INexusSession
{
    public long Id { get; set; }
    public List<int> RegisteredGroups { get; } = new();
    public Lock RegisteredGroupsLock { get; } = new();
    public ConnectionState State { get; set; } = ConnectionState.Connected;
    public INexusLogger? Logger { get; set; }

    // Track sent messages for verification
    public List<object> SentMessages { get; } = new();

    public ValueTask SendMessage<TMessage>(TMessage message, CancellationToken ct = default)
    {
        SentMessages.Add(message!);
        return ValueTask.CompletedTask;
    }

    // Mock SessionInvocationStateManager
    public SessionInvocationStateManager SessionInvocationStateManager { get; } = new();

    // Other INexusSession members...
    public IServerSessionManager? SessionManager { get; set; }
    public SessionStore SessionStore { get; } = new();
    public long LastReceived { get; set; }
    public CacheManager CacheManager { get; set; } = null!;
    public NexusCollectionManager CollectionManager { get; set; } = null!;
    public ConfigBase Config { get; set; } = null!;
    public bool IsServer { get; set; } = true;
    public NexusPipeManager PipeManager { get; set; } = null!;

    public bool DisconnectIfTimeout(long timeoutTicks) => false;
    public Task DisconnectAsync(DisconnectReason reason) => Task.CompletedTask;
}
```

### 4.2 LocalSessionRegistry Tests

```csharp
public class LocalSessionRegistryTests
{
    private LocalSessionContext _context;
    private LocalSessionRegistry _registry;

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
    public async Task UnregisterSessionAsync_ExistingSession_RemovesFromContext()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.RegisterSessionAsync(session);

        await _registry.UnregisterSessionAsync(session);

        Assert.That(_context.Sessions.ContainsKey(1), Is.False);
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
    public async Task GetSessionCountAsync_ReturnsCorrectCount()
    {
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 1 });
        await _registry.RegisterSessionAsync(new MockNexusSession { Id = 2 });

        var count = await _registry.GetSessionCountAsync();

        Assert.That(count, Is.EqualTo(2));
    }
}
```

### 4.3 LocalGroupRegistry Tests

```csharp
public class LocalGroupRegistryTests
{
    private LocalSessionContext _context;
    private LocalGroupRegistry _registry;

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
    }

    [Test]
    public async Task AddToGroupsAsync_MultipleGroups_AddsSessionToAll()
    {
        var session = new MockNexusSession { Id = 1 };

        await _registry.AddToGroupsAsync(new[] { "group1", "group2", "group3" }, session);

        Assert.That(session.RegisteredGroups, Has.Count.EqualTo(3));
        Assert.That(_registry.GetLocalGroupMembers("group1"), Contains.Item(session));
        Assert.That(_registry.GetLocalGroupMembers("group2"), Contains.Item(session));
        Assert.That(_registry.GetLocalGroupMembers("group3"), Contains.Item(session));
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
    public async Task RemoveFromAllGroupsAsync_SessionInMultipleGroups_RemovesFromAll()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupsAsync(new[] { "group1", "group2" }, session);

        await _registry.RemoveFromAllGroupsAsync(session);

        Assert.That(session.RegisteredGroups, Is.Empty);
        Assert.That(_registry.GetLocalGroupMembers("group1"), Does.Not.Contain(session));
        Assert.That(_registry.GetLocalGroupMembers("group2"), Does.Not.Contain(session));
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
    public async Task GetGroupNamesAsync_ReturnsAllGroupNames()
    {
        var session = new MockNexusSession { Id = 1 };
        await _registry.AddToGroupAsync("group1", session);
        await _registry.AddToGroupAsync("group2", session);

        var names = await _registry.GetGroupNamesAsync();

        Assert.That(names, Contains.Item("group1"));
        Assert.That(names, Contains.Item("group2"));
    }
}
```

### 4.4 LocalInvocationRouter Tests

```csharp
public class LocalInvocationRouterTests
{
    private LocalSessionContext _context;
    private LocalInvocationRouter _router;
    private LocalGroupRegistry _groupRegistry;

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
    public async Task InvokeClientsAsync_MultipleIds_SendsToAllExisting()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };
        _context.Sessions.TryAdd(1, session1);
        _context.Sessions.TryAdd(2, session2);

        var message = CreateTestMessage();
        await _router.InvokeClientsAsync(message, new[] { 1L, 2L, 999L }); // 999 doesn't exist

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
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
        await _router.InvokeGroupsAsync(message, new[] { "group1", "group2" });

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));
    }

    private static InvocationMessage CreateTestMessage()
    {
        return new InvocationMessage
        {
            MethodId = 1,
            InvocationId = 0,
            Flags = InvocationFlags.IgnoreReturn
        };
    }
}
```

### 4.5 LocalServerSessionManager Integration Tests

```csharp
public class LocalServerSessionManagerTests
{
    private LocalServerSessionManager _manager;

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
    }

    [Test]
    public async Task FullWorkflow_RegisterGroupInvokeUnregister()
    {
        var session1 = new MockNexusSession { Id = 1 };
        var session2 = new MockNexusSession { Id = 2 };

        // Register sessions
        await _manager.Sessions.RegisterSessionAsync(session1);
        await _manager.Sessions.RegisterSessionAsync(session2);

        // Add to group
        await _manager.Groups.AddToGroupAsync("players", session1);
        await _manager.Groups.AddToGroupAsync("players", session2);

        // Invoke on group
        var message = CreateTestMessage();
        await _manager.Router.InvokeGroupAsync(message, "players");

        Assert.That(session1.SentMessages, Has.Count.EqualTo(1));
        Assert.That(session2.SentMessages, Has.Count.EqualTo(1));

        // Unregister
        await _manager.Groups.RemoveFromAllGroupsAsync(session1);
        await _manager.Sessions.UnregisterSessionAsync(session1);

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

    private static InvocationMessage CreateTestMessage()
    {
        return new InvocationMessage
        {
            MethodId = 1,
            InvocationId = 0,
            Flags = InvocationFlags.IgnoreReturn
        };
    }
}
```

---

## 5. Configuration Changes

### 4.1 ServerConfig Modification

Add the session manager to `ServerConfig`:

```csharp
// In ServerConfig.cs
public abstract class ServerConfig : ConfigBase
{
    // ... existing properties ...

    /// <summary>
    /// The session manager implementation to use.
    /// Defaults to LocalServerSessionManager if not specified.
    /// </summary>
    public IServerSessionManager? SessionManager { get; set; }

    // Internal getter that provides default
    internal IServerSessionManager GetSessionManager()
    {
        return SessionManager ?? new LocalServerSessionManager();
    }
}
```

### 4.2 NexusServer Changes

Modify `NexusServer` to use the configured session manager:

```csharp
// In NexusServer.cs
public sealed class NexusServer<TServerNexus, TClientProxy> : INexusServer<TClientProxy>
{
    // Replace:
    // private readonly SessionManager _sessionManager = new();

    // With:
    private IServerSessionManager _sessionManager = null!;

    // In Configure():
    public void Configure(ServerConfig config, Func<TServerNexus> nexusFactory)
    {
        // ... existing code ...
        _sessionManager = config.GetSessionManager();
    }

    // In StartAsync():
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // ... existing code ...
        await _sessionManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        // ... rest of method ...
    }

    // In StopAsync():
    public async Task StopAsync()
    {
        // ... existing shutdown code ...
        await _sessionManager.ShutdownAsync().ConfigureAwait(false);
    }
}
```

---

## 5. GroupManager Changes

The public `GroupManager` API changes to async. **Sync methods are removed entirely** (no backward compatibility period).

```csharp
namespace NexNet.Invocation;

/// <summary>
/// Manager for a session's groups.
/// </summary>
public class GroupManager
{
    private readonly INexusSession _session;
    private readonly IGroupRegistry _groupRegistry;

    internal GroupManager(INexusSession session, IGroupRegistry groupRegistry)
    {
        _session = session;
        _groupRegistry = groupRegistry;
    }

    /// <summary>
    /// Adds the current session to a group.
    /// </summary>
    /// <param name="groupName">Group to add this session to.</param>
    public ValueTask AddAsync(string groupName)
    {
        return _groupRegistry.AddToGroupAsync(groupName, _session);
    }

    /// <summary>
    /// Adds the current session to multiple groups.
    /// </summary>
    /// <param name="groupNames">Groups to add this session to.</param>
    public ValueTask AddAsync(string[] groupNames)
    {
        return _groupRegistry.AddToGroupsAsync(groupNames, _session);
    }

    /// <summary>
    /// Removes the current session from a group.
    /// </summary>
    /// <param name="groupName">Group to remove this session from.</param>
    public ValueTask RemoveAsync(string groupName)
    {
        return _groupRegistry.RemoveFromGroupAsync(groupName, _session);
    }

    /// <summary>
    /// Gets all group names.
    /// </summary>
    /// <returns>Collection of group names.</returns>
    public ValueTask<IReadOnlyCollection<string>> GetNamesAsync()
    {
        return _groupRegistry.GetGroupNamesAsync();
    }
}
```

---

## 6. ProxyInvocationBase Changes

Modify `ProxyInvocationBase` to use `IInvocationRouter`:

```csharp
// In ProxyInvocationBase.cs

// Add field:
private IInvocationRouter? _invocationRouter;

// Modify Configure():
void IProxyInvoker.Configure(
    INexusSession? session,
    IServerSessionManager? sessionManager,  // Changed from SessionManager
    ProxyInvocationMode mode,
    object? modeArguments)
{
    _session = session;
    _invocationRouter = sessionManager;  // IServerSessionManager implements IInvocationRouter
    // ... rest of method ...
}

// Simplify ProxyInvokeMethodCore() - delegate to router:
async ValueTask IProxyInvoker.ProxyInvokeMethodCore(ushort methodId, ITuple? arguments, InvocationFlags flags)
{
    // ... message creation code stays the same ...

    switch (_mode)
    {
        case ProxyInvocationMode.Caller:
            await _session!.SendMessage(message).ConfigureAwait(false);
            break;

        case ProxyInvocationMode.All:
            if (flags.HasFlag(InvocationFlags.DuplexPipe))
                throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);
            // Invocation ID assignment handled internally by router
            await _invocationRouter!.InvokeAllAsync(message).ConfigureAwait(false);
            break;

        case ProxyInvocationMode.Others:
            if (flags.HasFlag(InvocationFlags.DuplexPipe))
                throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);
            await _invocationRouter!.InvokeAllExceptAsync(message, _session!.Id).ConfigureAwait(false);
            break;

        case ProxyInvocationMode.Client:
            await _invocationRouter!.InvokeClientAsync(message, _modeClientArguments![0]).ConfigureAwait(false);
            break;

        case ProxyInvocationMode.Clients:
            if (flags.HasFlag(InvocationFlags.DuplexPipe))
                throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);
            await _invocationRouter!.InvokeClientsAsync(message, _modeClientArguments!).ConfigureAwait(false);
            break;

        case ProxyInvocationMode.AllExcept:
            if (flags.HasFlag(InvocationFlags.DuplexPipe))
                throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);
            await _invocationRouter!.InvokeAllExceptAsync(message, _modeClientArguments![0]).ConfigureAwait(false);
            break;

        case ProxyInvocationMode.Groups:
            if (flags.HasFlag(InvocationFlags.DuplexPipe))
                throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);
            await _invocationRouter!.InvokeGroupsAsync(message, _modeGroupArguments!, null).ConfigureAwait(false);
            break;

        case ProxyInvocationMode.GroupsExceptCaller:
            if (flags.HasFlag(InvocationFlags.DuplexPipe))
                throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);
            await _invocationRouter!.InvokeGroupsAsync(message, _modeGroupArguments!, _session?.Id).ConfigureAwait(false);
            break;

        default:
            throw new ArgumentOutOfRangeException();
    }

    message.Dispose();
}
```

---

## 7. Files to Modify

| File | Changes |
|------|---------|
| `Invocation/SessionManager.cs` | Rename to `LocalServerSessionManager.cs`, implement `IServerSessionManager` |
| `Invocation/GroupManager.cs` | Change methods to async, use `IGroupRegistry` |
| `Invocation/ProxyInvocationBase.cs` | Use `IInvocationRouter` instead of direct `SessionManager` |
| `Invocation/IProxyInvoker.cs` | Update `Configure` signature |
| `Transports/ServerConfig.cs` | Add `SessionManager` property |
| `NexusServer.cs` | Use configured `IServerSessionManager` |
| `Invocation/ServerSessionContext.cs` | Pass `IServerSessionManager` to `GroupManager` |
| `Invocation/ServerNexusContext.cs` | Use `IServerSessionManager` |
| `Invocation/ServerNexusContextProvider.cs` | Use `IServerSessionManager` |
| `Cache/CachedProxy.cs` | Update `Rent` signature |
| `Internals/INexusSession.cs` | Change `SessionManager?` to `IServerSessionManager?` |

---

## 8. New Files to Create

| File | Purpose |
|------|---------|
| `Invocation/ISessionRegistry.cs` | Session registry interface |
| `Invocation/IGroupRegistry.cs` | Group registry interface |
| `Invocation/IInvocationRouter.cs` | Invocation routing interface |
| `Invocation/IServerSessionManager.cs` | Combined interface |
| `Invocation/LocalServerSessionManager.cs` | Default local implementation |

---

## 9. Breaking Changes

### Public API Changes

1. **GroupManager methods become async** (sync versions removed entirely):
   ```csharp
   // Before
   Context.Groups.Add("group");
   Context.Groups.Remove("group");
   var names = Context.Groups.GetNames();

   // After
   await Context.Groups.AddAsync("group");
   await Context.Groups.RemoveAsync("group");
   var names = await Context.Groups.GetNamesAsync();
   ```

2. **ServerConfig gains new property**:
   ```csharp
   public IServerSessionManager? SessionManager { get; set; }
   ```

### Internal API Changes

1. `IProxyInvoker.Configure` signature changes - `SessionManager` becomes `IServerSessionManager`
2. `INexusSession.SessionManager` type changes from `SessionManager?` to `IServerSessionManager?`
3. All proxy cache `Rent` methods updated to use `IServerSessionManager`
4. `SessionManager` class renamed to `LocalServerSessionManager` and implements `IServerSessionManager`

---

## 10. Migration Path

### Step 1: Create interfaces and local implementation
- Create `ISessionRegistry.cs`, `IGroupRegistry.cs`, `IInvocationRouter.cs`, `IServerSessionManager.cs`
- Create `LocalServerSessionManager.cs` based on current `SessionManager`

### Step 2: Update ServerConfig
- Add `SessionManager` property of type `IServerSessionManager?`
- Add internal `GetSessionManager()` helper that returns default if null

### Step 3: Update NexusServer
- Replace `SessionManager` field with `IServerSessionManager`
- Call `InitializeAsync()` in `StartAsync()`
- Call `ShutdownAsync()` in `StopAsync()`

### Step 4: Update GroupManager
- Remove all sync methods (`Add`, `Remove`, `GetNames`)
- Add async methods (`AddAsync`, `RemoveAsync`, `GetNamesAsync`)
- Change constructor to accept `IGroupRegistry`

### Step 5: Update ProxyInvocationBase
- Change `_sessionManager` field type to `IServerSessionManager?`
- Use `IInvocationRouter` methods for all routing
- Remove direct session iteration logic

### Step 6: Update remaining references
- `ServerSessionContext` - pass `IServerSessionManager` to `GroupManager`
- `ServerNexusContext` - use `IServerSessionManager`
- `ServerNexusContextProvider` - use `IServerSessionManager`
- `CachedProxy.Rent()` - update signature
- `INexusSession.SessionManager` - change type

### Step 7: Update tests
- Change all `Context.Groups.Add()` to `await Context.Groups.AddAsync()`
- Change all `Context.Groups.Remove()` to `await Context.Groups.RemoveAsync()`
- Change all `Context.Groups.GetNames()` to `await Context.Groups.GetNamesAsync()`

---

## 11. Future Backplane Implementation

With these interfaces in place, a future Garnet backplane would:

```csharp
public class GarnetServerSessionManager : IServerSessionManager
{
    private readonly IConnectionMultiplexer _garnet;
    private readonly LocalServerSessionManager _local; // For local session tracking

    // ISessionRegistry - local + Garnet registry
    public async ValueTask<bool> RegisterSessionAsync(INexusSession session)
    {
        // Register locally
        await _local.RegisterSessionAsync(session);

        // Register in Garnet
        await _garnet.GetDatabase().HashSetAsync($"sessions:{session.Id}", ...);
        return true;
    }

    // IInvocationRouter - local + pub/sub
    public async ValueTask InvokeAllAsync<TMessage>(TMessage message, ...)
    {
        // Invoke locally
        await _local.InvokeAllAsync(message, ...);

        // Publish to backplane for other servers
        await _garnet.GetSubscriber().PublishAsync("nexnet:invoke:all", ...);
    }

    // ... etc
}
```

The interfaces ensure that swapping implementations is straightforward.
