using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;

namespace NexNet.IntegrationTests;

/// <summary>
/// Validation of <see cref="HttpClientConfig.UpgradeHeaders"/> (S54): the same rules as the Java
/// <c>ClientConfig.Builder.header</c>.
/// </summary>
internal class UpgradeHeaderCollectionTests
{
    private static UpgradeHeaderCollection Headers()
        => new WebSocketClientConfig { Url = new Uri("ws://127.0.0.1:1/") }.UpgradeHeaders;

    [Test]
    public async Task EmptyByDefault()
    {
        await Assert.That(Headers().Count).IsEqualTo(0);
        await Assert.That(new HttpSocketClientConfig { Url = new Uri("http://127.0.0.1:1/") }.UpgradeHeaders.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("X-Tenant", "acme")]
    [Arguments("Authorization", "Bearer abc")]
    [Arguments("x!#$%&'*+-.^_`|~09", "v")]
    [Arguments("X-Tab", "a\tb")]
    [Arguments("X-Latin1", "caf\u00e9")]
    [Arguments("X-Empty", "")]
    public async Task AcceptsValidHeaders(string name, string value)
    {
        var headers = Headers();
        headers[name] = value;
        await Assert.That(headers[name]).IsEqualTo(value);
    }

    [Test]
    [Arguments("Host")]
    [Arguments("connection")]
    [Arguments("UPGRADE")]
    [Arguments("Sec-WebSocket-Key")]
    [Arguments("sec-websocket-protocol")]
    [Arguments("Content-Length")]
    [Arguments("Transfer-Encoding")]
    [Arguments("Content-Type")]
    [Arguments("Expires")]
    public async Task RefusesReservedNames(string name)
    {
        var headers = Headers();
        await Assert.That(() => headers[name] = "x").ThrowsExactly<ArgumentException>();
        await Assert.That(() => headers.Add(name, "x")).ThrowsExactly<ArgumentException>();
        await Assert.That(headers.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("")]
    [Arguments("X Space")]
    [Arguments("X:Colon")]
    [Arguments("X\r\nInjected")]
    [Arguments("X-\u00e9")]
    [Arguments("(X)")]
    public async Task RefusesInvalidNames(string name)
    {
        await Assert.That(() => Headers()[name] = "x").ThrowsExactly<ArgumentException>();
    }

    [Test]
    [Arguments("a\rb")]
    [Arguments("a\nb")]
    [Arguments("a\r\nX-Injected: 1")]
    [Arguments("a\u0000b")]
    [Arguments("a\u007fb")]
    [Arguments("\u0100")]
    [Arguments("\u20ac")]
    public async Task RefusesControlAndNonLatin1Values(string value)
    {
        await Assert.That(() => Headers()["X-Test"] = value).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task RefusesNull()
    {
        var headers = Headers();
        await Assert.That(() => headers[null!] = "x").ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => headers["X-Test"] = null!).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task SettingANameAgainIgnoringCaseReplacesIt()
    {
        var headers = Headers();
        headers["X-One"] = "1";
        headers["X-Two"] = "2";
        headers["x-one"] = "3";

        await Assert.That(headers.Count).IsEqualTo(2);
        await Assert.That(headers["X-ONE"]).IsEqualTo("3");
        await Assert.That(string.Join(",", headers.Keys)).IsEqualTo("X-Two,x-one");
    }

    [Test]
    public async Task AddRefusesANameAlreadySet()
    {
        var headers = Headers();
        headers.Add("X-One", "1");
        await Assert.That(() => headers.Add("x-ONE", "2")).ThrowsExactly<ArgumentException>();
        await Assert.That(headers["X-One"]).IsEqualTo("1");
    }

    [Test]
    public async Task RemoveAndLookupIgnoreCase()
    {
        var headers = Headers();
        headers["X-One"] = "1";
        await Assert.That(headers.ContainsKey("x-one")).IsTrue();
        await Assert.That(headers.TryGetValue("X-ONE", out var value)).IsTrue();
        await Assert.That(value).IsEqualTo("1");
        await Assert.That(headers.Remove("x-one")).IsTrue();
        await Assert.That(headers.Remove("X-One")).IsFalse();
        await Assert.That(() => headers["X-One"]).ThrowsExactly<KeyNotFoundException>();
    }

    [Test]
    public async Task CollectionInitializerSetsHeaders()
    {
        var config = new HttpSocketClientConfig
        {
            Url = new Uri("http://127.0.0.1:1/"),
            UpgradeHeaders = { ["X-Tenant"] = "acme", ["X-Region"] = "eu" },
        };
        await Assert.That(config.UpgradeHeaders.Count).IsEqualTo(2);
        await Assert.That(config.UpgradeHeaders["x-tenant"]).IsEqualTo("acme");
    }
}
