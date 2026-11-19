using System.Text.Json.Nodes;
using NexNet.Interop.Remote;
using NexNet.Invocation;
using NexNet.Serialization;
using NexNet.Transports;

namespace NexNet.Interop.Tests;

public class ControlConnectionTests
{
    [Test]
    public async Task Ping_RoundTrips()
    {
        await using var fake = await FakeConnection.OpenAsync();
        var result = await fake.Connection.RequestAsync(null, "ping", deadline: TimeSpan.FromSeconds(5));
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task HostReportedTimeout_IsATimeoutException()
    {
        await using var fake = await FakeConnection.OpenAsync();
        await fake.Connection.BeginTestAsync("t");
        var started = DateTime.UtcNow;
        await Assert.ThrowsExactlyAsync<TimeoutException>(async () => await fake.Connection.RequestAsync("t", "fake.hang", timeoutMs: 200));
        await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task NoResponse_TimesOutOnTheRunnerSide_AndALateResponseIsIgnored()
    {
        await using var fake = await FakeConnection.OpenAsync();
        var error = await Assert.ThrowsExactlyAsync<TimeoutException>(
            async () => await fake.Connection.RequestAsync(null, "fake.sleep", new JsonObject { ["ms"] = 600 }, deadline: TimeSpan.FromMilliseconds(150)));
        await Assert.That(error!.Message).Contains("fake.sleep");

        // The late response arrives and is dropped; the connection stays usable.
        await Task.Delay(700);
        await Assert.That(fake.Connection.IsClosed).IsFalse();
        await fake.Connection.RequestAsync(null, "ping", deadline: TimeSpan.FromSeconds(5));
        await Assert.That(fake.Connection.Transcript.Get(null).Any(l => l.Contains("no response to request"))).IsTrue();
    }

    [Test]
    public async Task UnknownScope_IsABadRequest()
    {
        await using var fake = await FakeConnection.OpenAsync();
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await fake.Connection.RequestAsync("never-begun", "client.state", new JsonObject { ["h"] = "c1" }));
    }

    [Test]
    public async Task UnknownHandle_IsAnInvalidOperation()
    {
        await using var fake = await FakeConnection.OpenAsync();
        await fake.Connection.BeginTestAsync("t");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await fake.Connection.RequestAsync("t", "client.state", new JsonObject { ["h"] = "c999" }));
    }

    [Test]
    public async Task Requests_AreWrittenInCallOrder()
    {
        await using var fake = await FakeConnection.OpenAsync();
        var tasks = Enumerable.Range(0, 50)
            .Select(i => fake.Connection.RequestAsync(null, i % 2 == 0 ? "ping" : "log.level", new JsonObject { ["level"] = "Debug" }))
            .ToArray();
        await Task.WhenAll(tasks);
        var ops = fake.Host.Ops.ToArray();
        for (var i = 0; i < 50; i++)
            await Assert.That(ops[i]).IsEqualTo(i % 2 == 0 ? "ping" : "log.level");
    }

    [Test]
    public async Task Events_AreRoutedByScopeAndHandle_AndRecorded()
    {
        await using var fake = await FakeConnection.OpenAsync();
        var mine = new List<ControlEvent>();
        var unrouted = new List<ControlEvent>();
        fake.Connection.UnroutedEvent += unrouted.Add;
        using (fake.Connection.Subscribe("t", "c1", e => mine.Add(e)))
        {
            fake.Host.SendEvent("t", "c1", "client.state", new JsonObject { ["state"] = "Connected" });
            fake.Host.SendEvent("t", "c2", "client.state", new JsonObject { ["state"] = "Connected" });
            fake.Host.SendEvent(null, null, "log", new JsonObject { ["message"] = "hello" });
            await fake.Connection.RequestAsync(null, "ping", deadline: TimeSpan.FromSeconds(5)); // events precede this response
        }

        await Assert.That(mine.Count).IsEqualTo(1);
        await Assert.That(mine[0].Data["state"]!.GetValue<string>()).IsEqualTo("Connected");
        await Assert.That(unrouted.Select(e => e.Handle ?? e.Name)).IsEquivalentTo(new[] { "c2", "log" });
        await Assert.That(fake.Connection.Transcript.Get("t").Count).IsEqualTo(2);
    }

    [Test]
    public async Task ClosedByTheHost_FailsRequestsWithHostCrashed()
    {
        await using var fake = await FakeConnection.OpenAsync();
        var pending = fake.Connection.RequestAsync(null, "fake.hang");
        fake.Host.Crash();
        await Assert.ThrowsExactlyAsync<HostCrashedException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsExactlyAsync<HostCrashedException>(async () => await fake.Connection.RequestAsync(null, "ping"));
    }

    [Test]
    public async Task ClientErrorKinds_MapToTheCSharpClientsExceptions()
    {
        static Exception Map(string kind, JsonObject? detail = null) => ControlErrors.ToException(ControlErrors.Create(kind, "m", detail));

        await Assert.That(Map("Transport", new JsonObject { ["reason"] = "AuthenticationError" })).IsTypeOf<TransportException>();
        await Assert.That(((TransportException)Map("Transport", new JsonObject { ["reason"] = "AuthenticationError" })).Error).IsEqualTo(TransportError.AuthenticationError);
        var remote = (ProxyRemoteInvocationException)Map("RemoteInvocation", new JsonObject { ["type"] = "X", ["code"] = 7 });
        await Assert.That(remote.RemoteExceptionType).IsEqualTo("X");
        await Assert.That(remote.RemoteErrorCode).IsEqualTo(7);
        await Assert.That(Map("Unauthorized")).IsTypeOf<ProxyUnauthorizedException>();
        await Assert.That(Map("Canceled")).IsTypeOf<TaskCanceledException>();
        await Assert.That(Map("Disconnected").Message).IsEqualTo(ErrorKinds.NotConnectedMessage);
        await Assert.That(Map("Serialization")).IsTypeOf<NexusSerializationException>();
        await Assert.That(Map("Busy")).IsTypeOf<NexusBusyException>();
        var tooLarge = (NexusMessageTooLargeException)Map("TooLarge", new JsonObject { ["size"] = 10, ["limit"] = 5 });
        await Assert.That(tooLarge.Size).IsEqualTo(10L);
        await Assert.That(Map("InvalidOperation")).IsTypeOf<InvalidOperationException>();
        var outOfRange = (ArgumentOutOfRangeException)Map("ArgumentOutOfRange", new JsonObject { ["param"] = "index" });
        await Assert.That(outOfRange.ParamName).IsEqualTo("index");

        await Assert.That(Map("Timeout")).IsTypeOf<TimeoutException>();
        await Assert.That(Map("NotSupported")).IsTypeOf<NotSupportedException>();
        await Assert.That(Map("BadRequest")).IsTypeOf<ArgumentException>();
        await Assert.That(Map("UnknownHandle")).IsTypeOf<InvalidOperationException>();
        var internalError = (RemoteHostException)Map("Internal", new JsonObject { ["stack"] = "at host" });
        await Assert.That(internalError.RemoteStack).IsEqualTo("at host");
        await Assert.That(((RemoteHostException)Map("SomethingNew")).Kind).IsEqualTo("SomethingNew");
    }

    [Test]
    public async Task RemoteInvocation_NullOrMissingMessage_IsANullRemoteMessage()
    {
        var withMessage = (ProxyRemoteInvocationException)ControlErrors.ToException(ControlErrors.Create("RemoteInvocation", "boom"));
        await Assert.That(withMessage.RemoteMessage).IsEqualTo("boom");

        var nullMessage = (ProxyRemoteInvocationException)ControlErrors.ToException(
            ControlErrors.Create("RemoteInvocation", null, new JsonObject { ["code"] = 7 }));
        await Assert.That(nullMessage.RemoteMessage).IsNull();
        await Assert.That(nullMessage.RemoteErrorCode).IsEqualTo(7);

        var missing = (ProxyRemoteInvocationException)ControlErrors.ToException(
            new JsonObject { ["kind"] = "RemoteInvocation", ["detail"] = new JsonObject { ["type"] = "X" } });
        await Assert.That(missing.RemoteMessage).IsNull();
        await Assert.That(missing.RemoteExceptionType).IsEqualTo("X");

        // A C# host sends null when the server sent no message, and the remote message otherwise.
        var none = ControlErrors.FromException(new ProxyRemoteInvocationException(7, null, null));
        await Assert.That(none.ContainsKey("message")).IsTrue();
        await Assert.That(none["message"]).IsNull();
        await Assert.That(((ProxyRemoteInvocationException)ControlErrors.ToException(none)).RemoteMessage).IsNull();
        var some = ControlErrors.FromException(new ProxyRemoteInvocationException(null, "T", "m"));
        await Assert.That(some["message"]!.GetValue<string>()).IsEqualTo("m");
    }

    [Test]
    public async Task ErrorObjects_RoundTripCSharpExceptions()
    {
        var error = ControlErrors.FromException(new NexusMessageTooLargeException(100, 50));
        await Assert.That(error["kind"]!.GetValue<string>()).IsEqualTo("TooLarge");
        var back = (NexusMessageTooLargeException)ControlErrors.ToException(error);
        await Assert.That(back.Limit).IsEqualTo(50L);

        await Assert.That(ControlErrors.FromException(new TimeoutException("t"))["kind"]!.GetValue<string>()).IsEqualTo(ControlErrors.Timeout);
        await Assert.That(ControlErrors.FromException(new InvalidOperationException(ErrorKinds.NotConnectedMessage))["kind"]!.GetValue<string>())
            .IsEqualTo("Disconnected");

        // A client argument error is a client kind, not a malformed request; other argument errors stay BadRequest.
        var argument = ControlErrors.FromException(new ArgumentOutOfRangeException("index", "Index must not be negative."));
        await Assert.That(argument["kind"]!.GetValue<string>()).IsEqualTo("ArgumentOutOfRange");
        await Assert.That(argument["message"]!.GetValue<string>()).IsEqualTo("Index must not be negative.");
        var argumentBack = (ArgumentOutOfRangeException)ControlErrors.ToException(argument);
        await Assert.That(argumentBack.ParamName).IsEqualTo("index");
        await Assert.That(argumentBack.Message).IsEqualTo(new ArgumentOutOfRangeException("index", "Index must not be negative.").Message);
        await Assert.That(ControlErrors.FromException(new ArgumentException("bad"))["kind"]!.GetValue<string>()).IsEqualTo(ControlErrors.BadRequest);
    }
}
