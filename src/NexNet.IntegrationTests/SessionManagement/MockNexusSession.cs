using System.Buffers;
using System.Runtime.CompilerServices;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Pools;
using NexNet.Transports;

namespace NexNet.IntegrationTests.SessionManagement;

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

    // Flag to simulate send failures
    public bool ShouldFailSend { get; set; }

    public ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default)
        where TMessage : IMessageBase
    {
        if (ShouldFailSend)
            throw new InvalidOperationException("Simulated send failure");

        SentMessages.Add(body!);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
    {
        if (ShouldFailSend)
            throw new InvalidOperationException("Simulated send failure");

        return ValueTask.CompletedTask;
    }

    public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default)
    {
        if (ShouldFailSend)
            throw new InvalidOperationException("Simulated send failure");

        return ValueTask.CompletedTask;
    }

    public ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
    {
        if (ShouldFailSend)
            throw new InvalidOperationException("Simulated send failure");

        return ValueTask.CompletedTask;
    }

    public Task DisconnectAsync(DisconnectReason reason, [CallerFilePath] string? filePath = null, [CallerLineNumber] int? lineNumber = null)
    {
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    // Other INexusSession members with minimal implementations
    public IServerSessionManager? SessionManager { get; set; }
    public SessionStore SessionStore { get; } = new();
    public long LastReceived { get; set; }
    public PoolManager PoolManager { get; set; } = null!;
    public NexusCollectionManager CollectionManager { get; set; } = null!;
    public ConfigBase Config { get; set; } = null!;
    public bool IsServer { get; set; } = true;
    public NexusPipeManager PipeManager { get; set; } = null!;
    public string? RemoteAddress { get; set; } = "127.0.0.1";
    public int? RemotePort { get; set; } = 12345;

    // Mock SessionInvocationStateManager
    private MockSessionInvocationStateManager? _sessionInvocationStateManager;

    public ISessionInvocationStateManager SessionInvocationStateManager =>
        _sessionInvocationStateManager ??= new MockSessionInvocationStateManager();

    public bool DisconnectIfTimeout(long timeoutTicks) => false;
}

/// <summary>
/// Mock implementation of ISessionInvocationStateManager for testing.
/// </summary>
internal class MockSessionInvocationStateManager : ISessionInvocationStateManager
{
    private ushort _invocationId = 0;

    public ushort GetNextId(bool addToCurrentInvocations)
    {
        return ++_invocationId;
    }

    public void UpdateInvocationResult(InvocationResultMessage message)
    {
        // No-op for testing
    }

    public ValueTask<RegisteredInvocationState?> InvokeMethodWithResultCore(
        ushort methodId,
        ITuple? arguments,
        INexusSession session,
        CancellationToken? cancellationToken = null)
    {
        // No-op for testing - return null
        return ValueTask.FromResult<RegisteredInvocationState?>(null);
    }

    public void CancelAll()
    {
        // No-op for testing
    }
}
