using NexNet.Messages;
using NexNet.Pools;

namespace NexNet.IntegrationTests.SessionManagement;

/// <summary>
/// Mock invocation message for testing router implementations.
/// </summary>
internal class MockInvocationMessage : IInvocationMessage, IMessageBase
{
    public static MessageType Type => MessageType.Invocation;

    public ushort InvocationId { get; set; }
    public ushort MethodId { get; set; }
    public InvocationFlags Flags { get; set; } = InvocationFlags.IgnoreReturn;
    public Memory<byte> Arguments { get; set; } = Memory<byte>.Empty;

    public IPooledMessage? MessageCache { get; set; }

    public T? DeserializeArguments<T>()
    {
        return default;
    }

    public void Dispose()
    {
        // No-op for mock
    }
}
