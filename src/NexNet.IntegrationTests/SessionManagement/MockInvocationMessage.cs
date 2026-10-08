using NexNet.Messages;
using NexNet.Pools;
using NexNet.Serialization;

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

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(4);
        writer.Write(InvocationId);
        writer.Write(MethodId);
        writer.Write((byte)Flags);
        writer.WriteArrayHeader(0);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.Skip();
    }

    public void Dispose()
    {
        // No-op for mock
    }
}
