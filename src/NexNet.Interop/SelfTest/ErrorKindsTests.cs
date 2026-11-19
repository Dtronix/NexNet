using NexNet.Invocation;
using NexNet.Serialization;
using NexNet.Transports;

namespace NexNet.Interop.SelfTest;

public class ErrorKindsTests
{
    [Test]
    [Arguments(ErrorKind.Transport, typeof(TransportException))]
    [Arguments(ErrorKind.RemoteInvocation, typeof(ProxyRemoteInvocationException))]
    [Arguments(ErrorKind.Unauthorized, typeof(ProxyUnauthorizedException))]
    [Arguments(ErrorKind.Canceled, typeof(TaskCanceledException))]
    [Arguments(ErrorKind.Disconnected, typeof(InvalidOperationException))]
    [Arguments(ErrorKind.Serialization, typeof(NexusSerializationException))]
    [Arguments(ErrorKind.Busy, typeof(NexusBusyException))]
    [Arguments(ErrorKind.TooLarge, typeof(NexusMessageTooLargeException))]
    [Arguments(ErrorKind.InvalidOperation, typeof(InvalidOperationException))]
    public async Task EveryKind_MapsToItsExceptionAndBack(ErrorKind kind, Type expected)
    {
        var exception = ErrorKinds.ToException(new RemoteErrorInfo(kind, "boom") { Reason = "ConnectionRefused", Code = 7, Type = "X" });
        await Assert.That(exception.GetType()).IsEqualTo(expected);
        await Assert.That(ErrorKinds.FromException(exception).Kind).IsEqualTo(kind);
    }

    [Test]
    public async Task Details_RoundTrip()
    {
        var remote = (ProxyRemoteInvocationException)ErrorKinds.ToException(
            new RemoteErrorInfo(ErrorKind.RemoteInvocation, "bad") { Type = "System.ArgumentException", Code = 42 });
        await Assert.That(remote.RemoteMessage).IsEqualTo("bad");
        await Assert.That(remote.RemoteErrorCode).IsEqualTo(42);

        var transport = (TransportException)ErrorKinds.ToException(new RemoteErrorInfo(ErrorKind.Transport, "no") { Reason = "connectionrefused" });
        await Assert.That(transport.Error).IsEqualTo(TransportError.ConnectionRefused);

        var tooLarge = ErrorKinds.FromException(new NexusMessageTooLargeException(10, 5));
        await Assert.That(tooLarge.Size).IsEqualTo(10);
        await Assert.That(tooLarge.Limit).IsEqualTo(5);
    }
}
