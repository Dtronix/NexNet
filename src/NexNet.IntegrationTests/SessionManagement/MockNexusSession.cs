using System.Buffers;
using System.Runtime.CompilerServices;
using NexNet.Diagnostics;
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

    public SessionBudget Budget { get; set; } = new(16 * 1024 * 1024);

    public int SendMaxMessageSize => Config.MaxMessageSize;

    public SessionControlSender ControlSender => _controlSender ??= new SessionControlSender(this);

    private MetricsTagSet? _metricsTags;
    public MetricsTagSet MetricsTags => _metricsTags ??= NexusMetrics.GetTagSet(Config);
    private SessionControlSender? _controlSender;

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

    public async ValueTask<bool> SendInvocation<TMessage>(TMessage message, CancellationToken cancellationToken = default, TimeSpan? creditTimeout = null)
        where TMessage : IMessageBase
    {
        await SendMessage(message, cancellationToken);
        return true;
    }

    public InvocationWriteResult TryWriteInvocation<TMessage>(TMessage message, ReadOnlySpan<byte> frame = default)
        where TMessage : IInvocationMessage, IMessageBase
    {
        if (ShouldFailSend)
            throw new InvalidOperationException("Simulated send failure");

        SentMessages.Add(message!);
        return InvocationWriteResult.Written;
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
    public ConfigBase Config { get; set; } = new TcpServerConfig { EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0) };
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

    public bool IsOutstanding(ushort invocationId) => false;

    public void FailInvocation(ushort invocationId, Exception exception)
    {
        // No-op for testing
    }

    public void ReleaseId(ushort invocationId)
    {
        // No-op for testing
    }

    public ValueTask<RegisteredInvocationState?> InvokeMethodWithResultCore(
        ushort methodId,
        Memory<byte> serializedArguments,
        INexusSession session,
        CancellationToken? cancellationToken = null,
        NexNet.Serialization.PooledArrayBufferWriter? argumentsOwner = null)
    {
        argumentsOwner?.Return();
        // No-op for testing - return null
        return ValueTask.FromResult<RegisteredInvocationState?>(null);
    }

    public void CancelAll()
    {
        // No-op for testing
    }
}
