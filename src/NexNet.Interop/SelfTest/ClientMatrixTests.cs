namespace NexNet.Interop.SelfTest;

public class ClientMatrixTests
{
    [Test]
    public async Task Parse_AlwaysStartsWithCSharp()
    {
        await Assert.That(ClientLanguages.Parse(null)).IsEquivalentTo(new[] { "csharp" });
        await Assert.That(ClientLanguages.Parse(" ")).IsEquivalentTo(new[] { "csharp" });
        await Assert.That(ClientLanguages.Parse("node, Java,node"))
            .IsEquivalentTo(new[] { "csharp", "node", "java" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(ClientLanguages.Parse("browser"))
            .IsEquivalentTo(new[] { "csharp", "browser-chromium" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(ClientLanguages.Parse("browsers").Count).IsEqualTo(4);
    }

    [Test]
    public async Task Default_IsCSharpOverTheTlsHttpTransports()
    {
        var rows = new ClientMatrixAttribute { Languages = ["csharp"] }.GetTargets(transportOverride: null);
        await Assert.That(rows).IsEquivalentTo(
            new[] { ClientTarget.CSharp(ClientTransport.HttpSocketTls), ClientTarget.CSharp(ClientTransport.WebSocketTls) },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task EnvironmentUnset_OnlyCSharpRows()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ClientLanguages.EnvironmentVariable)))
            Skip.Test(ClientLanguages.EnvironmentVariable + " is set.");

        var rows = new ClientMatrixAttribute { Transports = [ClientTransport.Tcp, ClientTransport.Uds] }.GetTargets();
        await Assert.That(rows.Select(r => r.Language).Distinct()).IsEquivalentTo(new[] { "csharp" });
        await Assert.That(rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task TransportOverride_All_IsEveryTransportOfEachLanguage()
    {
        var rows = new ClientMatrixAttribute { Languages = ["csharp", "browser-chromium", "java"] }.GetTargets("ALL");

        ClientTransport[] Of(string language) => rows.Where(r => r.Language == language).Select(r => r.Transport).ToArray();

        await Assert.That(Of("csharp")).IsEquivalentTo(ClientTransports.All, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Of("browser-chromium")).IsEquivalentTo(
            new[] { ClientTransport.WebSocket, ClientTransport.WebSocketTls }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Of("java")).DoesNotContain(ClientTransport.Uds);
        await Assert.That(Of("java")).DoesNotContain(ClientTransport.Quic);
        await Assert.That(Of("java").Length).IsEqualTo(ClientTransports.All.Count - 2);
        await Assert.That(rows.Where(r => r.Transport != ClientTransport.Quic).All(r => r.SkipReason == null)).IsTrue();
    }

    [Test]
    public async Task TransportOverride_List_IsUsedForEveryLanguage()
    {
        var rows = new ClientMatrixAttribute { Languages = ["csharp", "browser-chromium"] }.GetTargets(" tcp, WebSocket ,Tcp");

        await Assert.That(rows.Select(r => r.ToString())).IsEquivalentTo(
            new[] { "csharp/Tcp", "csharp/WebSocket", "browser-chromium/Tcp", "browser-chromium/WebSocket" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rows.Single(r => r.ToString() == "browser-chromium/Tcp").SkipReason).Contains("no Tcp transport");
    }

    [Test]
    public async Task TransportOverride_DoesNotReplaceExplicitTransports()
    {
        var rows = new ClientMatrixAttribute { Languages = ["csharp"], Transports = [ClientTransport.Uds] }.GetTargets("all");
        await Assert.That(rows).IsEquivalentTo(new[] { ClientTarget.CSharp(ClientTransport.Uds) });
    }

    [Test]
    public async Task TransportOverride_UnsetOrBlank_IsTheDefault()
    {
        var csharp = ClientLanguages.Get("csharp");
        await Assert.That(ClientTransports.Select(csharp, null)).IsEquivalentTo(ClientTransports.Default);
        await Assert.That(ClientTransports.Select(csharp, " ")).IsEquivalentTo(ClientTransports.Default);
        await Assert.That(ClientTransports.Select(csharp, ",")).IsEquivalentTo(ClientTransports.Default);
    }

    [Test]
    public async Task TransportOverride_UnknownTransport_Throws()
    {
        var error = await Assert.That(() => ClientTransports.Select(ClientLanguages.Get("csharp"), "Tcp,Smoke"))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(error!.Message).Contains("Smoke").And.Contains(ClientTransports.EnvironmentVariable);
        await Assert.That(() => ClientTransports.Select(ClientLanguages.Get("csharp"), "2")).ThrowsExactly<ArgumentException>();
    }

    [Test]
    [ClientMatrix(Languages = ["csharp", "browser-chromium"], Transports = [ClientTransport.Tcp])]
    public async Task Rows_CarryTheLangAndTransportProperties(ClientTarget target)
    {
        var properties = TestContext.Current!.Metadata.TestDetails.CustomProperties;
        await Assert.That(properties[ClientMatrixAttribute.LanguageProperty]).IsEquivalentTo(new[] { target.Language });
        await Assert.That(properties[ClientMatrixAttribute.TransportProperty]).IsEquivalentTo(new[] { target.Transport.ToString() });
    }

    [Test]
    public async Task MissingTransportOrCapability_IsAVisibleSkip()
    {
        var rows = new ClientMatrixAttribute
        {
            Languages = ["csharp", "browser-chromium", "java"],
            Transports = [ClientTransport.Uds, ClientTransport.WebSocketTls],
            Requires = ClientCapabilities.Pinning,
        }.GetTargets();

        string? Reason(string language, ClientTransport transport)
            => rows.Single(r => r.Language == language && r.Transport == transport).SkipReason;

        await Assert.That(rows.Count).IsEqualTo(6);
        await Assert.That(Reason("csharp", ClientTransport.Uds)).IsNull();
        await Assert.That(Reason("csharp", ClientTransport.WebSocketTls)).IsNull();
        await Assert.That(Reason("browser-chromium", ClientTransport.Uds)).Contains("no Uds transport");
        await Assert.That(Reason("browser-chromium", ClientTransport.WebSocketTls)).Contains("Pinning");
        await Assert.That(Reason("java", ClientTransport.Uds)).Contains("no Uds transport");
        await Assert.That(Reason("java", ClientTransport.WebSocketTls)).IsNull();
    }

    private static readonly int[] Sizes = [1, 2];

    private static IEnumerable<string> Modes() => ["a", "b"];

    [Test]
    public async Task WithDataSource_IsTargetsTimesValues()
    {
        var attribute = new ClientMatrixAttribute<int>(nameof(Sizes)) { Languages = ["csharp", "browser-chromium"], Transports = [ClientTransport.Tcp, ClientTransport.WebSocket] };
        var rows = attribute.GetRows(attribute.GetValues(typeof(ClientMatrixTests)), transportOverride: null);

        await Assert.That(rows.Select(r => r.Target + ":" + r.Value)).IsEquivalentTo(
            new[]
            {
                "csharp/Tcp:1", "csharp/Tcp:2", "csharp/WebSocket:1", "csharp/WebSocket:2",
                "browser-chromium/Tcp:1", "browser-chromium/Tcp:2", "browser-chromium/WebSocket:1", "browser-chromium/WebSocket:2",
            },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rows.Single(r => r.Target.ToString() == "browser-chromium/Tcp" && r.Value == 1).Target.SkipReason).Contains("no Tcp transport");
        await Assert.That(rows.Single(r => r.Target.ToString() == "csharp/Tcp" && r.Value == 2).Target.SkipReason).IsNull();
    }

    [Test]
    public async Task WithDataSource_ReadsMethodsAndRejectsMissingMembers()
    {
        await Assert.That(new ClientMatrixAttribute<string>(nameof(Modes)).GetValues(typeof(ClientMatrixTests)))
            .IsEquivalentTo(new[] { "a", "b" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        var error = await Assert.That(() => new ClientMatrixAttribute<int>("Nope").GetValues(typeof(ClientMatrixTests)))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("Nope");
        await Assert.That(() => new ClientMatrixAttribute<int>(nameof(Modes)).GetValues(typeof(ClientMatrixTests)).ToArray())
            .Throws<InvalidCastException>();
    }

    [Test]
    [ClientMatrix<int>(nameof(Sizes), Languages = ["csharp", "browser-chromium"], Transports = [ClientTransport.Tcp])]
    public async Task WithDataSource_RowsCarryTheValueAndTheProperties(ClientTarget target, int size)
    {
        await Assert.That(Sizes).Contains(size);
        var properties = TestContext.Current!.Metadata.TestDetails.CustomProperties;
        await Assert.That(properties[ClientMatrixAttribute.LanguageProperty]).IsEquivalentTo(new[] { target.Language });
        await Assert.That(properties[ClientMatrixAttribute.TransportProperty]).IsEquivalentTo(new[] { target.Transport.ToString() });
    }

    [Test]
    public async Task CSharpTransports_AddCSharpRowsOnly()
    {
        var rows = new ClientMatrixAttribute
        {
            Languages = ["csharp", "node", "java"],
            Transports = [ClientTransport.Tcp, ClientTransport.WebSocketTls],
            CSharpTransports = [ClientTransport.Quic, ClientTransport.Tcp],
        }.GetTargets();

        await Assert.That(rows.Select(r => r.ToString())).IsEquivalentTo(
            new[] { "csharp/Tcp", "csharp/WebSocketTls", "csharp/Quic", "node/Tcp", "node/WebSocketTls", "java/Tcp", "java/WebSocketTls" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task CSharpTransports_AddToTheEnvironmentTransports()
    {
        var rows = new ClientMatrixAttribute { Languages = ["csharp"], CSharpTransports = [ClientTransport.Quic] }.GetTargets(" tcp ");
        await Assert.That(rows).IsEquivalentTo(
            new[] { ClientTarget.CSharp(ClientTransport.Tcp), ClientTarget.CSharp(ClientTransport.Quic) },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Quic_IsCSharpOnly_AndSkippedWithoutMsQuic()
    {
        var rows = new ClientMatrixAttribute
        {
            Languages = ["csharp", "node", "java", "browser-chromium"],
            Transports = [ClientTransport.Quic],
        }.GetTargets();

        string? Reason(string language) => rows.Single(r => r.Language == language).SkipReason;

        if (NexNet.InteropServer.InteropEndpointKinds.IsQuicSupported)
            await Assert.That(Reason("csharp")).IsNull();
        else
            await Assert.That(Reason("csharp")).Contains("msquic");
        await Assert.That(Reason("node")).Contains("no Quic transport");
        await Assert.That(Reason("java")).Contains("no Quic transport");
        await Assert.That(Reason("browser-chromium")).Contains("no Quic transport");
        await Assert.That(ClientTransport.Quic.IsSecure()).IsTrue();
        await Assert.That(ClientTransport.Quic.RequiredCapability()).IsEqualTo(ClientCapabilities.Quic);
    }

    [Test]
    public async Task UnknownLanguage_FailsVisibly()
    {
        var target = new ClientTarget("cobol", ClientTransport.Tcp);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Harness.CreateClientAsync(target, new ClientSpec()));
        await Assert.That(error!.Message).Contains("cobol");
    }

    [Test]
    [ClientMatrix(Languages = ["browser-chromium"], Transports = [ClientTransport.Uds])]
    public async Task UnsupportedRow_IsSkipped(ClientTarget target)
    {
        await Harness.CreateClientAsync(target, new ClientSpec());
        throw new InvalidOperationException("Not reached: the row is skipped.");
    }
}
