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

        ClientTransport[] Of(string language) => rows.Where(r => r.Language == language && r.Variant == null).Select(r => r.Transport).ToArray();

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
        var plan = new ClientMatrixAttribute { Languages = ["csharp", "browser-chromium"] }.Plan(" tcp, WebSocket ,Tcp");

        await Assert.That(plan.Rows.Select(r => r.ToString())).IsEquivalentTo(
            new[] { "csharp/Tcp", "csharp/WebSocket", "browser-chromium/WebSocket" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(plan.NotApplicable).IsEquivalentTo(new[]
        {
            new NotApplicableRow("browser-chromium", ClientTransport.Tcp, "The browser-chromium client has no Tcp transport."),
        });
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
    public async Task MissingTransportOrCapability_GetsNoRow_AndIsRecordedWithItsReason()
    {
        var plan = new ClientMatrixAttribute
        {
            Languages = ["csharp", "browser-chromium", "java"],
            Transports = [ClientTransport.Uds, ClientTransport.WebSocketTls],
            Requires = ClientCapabilities.Pinning,
        }.Plan(transportOverride: null, ClientLanguages.GetBuiltIn);

        await Assert.That(plan.Rows.Select(r => r.ToString())).IsEquivalentTo(
            new[] { "csharp/Uds", "csharp/WebSocketTls", "java/WebSocketTls", "java/WebSocketTls(okhttp)" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(plan.Rows.All(r => r.SkipReason == null)).IsTrue();
        await Assert.That(plan.NotApplicable).IsEquivalentTo(
            new[]
            {
                new NotApplicableRow("browser-chromium", ClientTransport.Uds, "The browser-chromium client has no Uds transport."),
                new NotApplicableRow("browser-chromium", ClientTransport.WebSocketTls, "The browser-chromium client lacks: Pinning."),
                new NotApplicableRow("java", ClientTransport.Uds, "The java client has no Uds transport."),
            },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task JavaWebSocketRows_HaveAnOkHttpVariant()
    {
        var rows = new ClientMatrixAttribute
        {
            Languages = ["java", "node"],
            Transports = [ClientTransport.Tcp, ClientTransport.WebSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls],
        }.Plan(transportOverride: null, ClientLanguages.GetBuiltIn).Rows;

        await Assert.That(rows.Select(r => r.ToString())).IsEquivalentTo(
            new[]
            {
                "java/Tcp", "java/WebSocket", "java/WebSocket(okhttp)", "java/HttpSocketTls", "java/WebSocketTls", "java/WebSocketTls(okhttp)",
                "node/Tcp", "node/WebSocket", "node/HttpSocketTls", "node/WebSocketTls",
            },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rows.Where(r => r.Variant != null).All(r => r.Variant == ClientVariant.OkHttp)).IsTrue();
        await Assert.That(ClientLanguages.GetBuiltIn("java").Variants.Single().Transports)
            .IsEquivalentTo(new[] { ClientTransport.WebSocket, ClientTransport.WebSocketTls });
    }

    [Test]
    public async Task Variant_IsPassedToTheDriverAsWebSocketConnector()
    {
        const string language = "variant-probe";
        var drivers = new SpecCapturingDriverFactory();
        ClientLanguages.Register(new ClientLanguageInfo(language, [ClientTransport.Tcp, ClientTransport.WebSocket], ClientCapabilities.Tcp)
        {
            Variants = [new ClientVariant("fast", [ClientTransport.WebSocket])],
        });
        Harness.RegisterDriver(language, drivers);

        var rows = new ClientMatrixAttribute { Languages = [language], Transports = [ClientTransport.Tcp, ClientTransport.WebSocket] }.GetTargets(transportOverride: null);
        await Assert.That(rows.Select(r => r.ToString())).IsEquivalentTo(
            new[] { language + "/Tcp", language + "/WebSocket", language + "/WebSocket(fast)" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);

        foreach (var row in rows)
        {
            var spec = new ClientSpec();
            await Assert.That(async () => await Harness.CreateClientAsync(row, spec)).ThrowsExactly<NotSupportedException>();
            await Assert.That(spec.WebSocketConnector).IsNull();
        }

        await Assert.That(drivers.Specs.Select(s => (s.Transport, s.WebSocketConnector))).IsEquivalentTo(
            new (ClientTransport?, string?)[] { (ClientTransport.Tcp, null), (ClientTransport.WebSocket, null), (ClientTransport.WebSocket, "fast") },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>A driver that records the spec it is given and creates nothing.</summary>
    private sealed class SpecCapturingDriverFactory : IClientDriverFactory
    {
        public List<ClientSpec> Specs { get; } = [];

        public Task<IClientUnderTest> CreateAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec)
            where TClientNexus : NexNet.Invocation.ClientNexusBase<TServerProxy>, NexNet.Invocation.IInvocationMethodHash, NexNet.Collections.ICollectionConfigurer, new()
            where TServerProxy : NexNet.Invocation.ProxyInvocationBase, NexNet.Invocation.IInvocationMethodHash, new()
        {
            lock (Specs)
                Specs.Add(spec);
            return Task.FromException<IClientUnderTest>(new NotSupportedException("Records the spec only."));
        }
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
                "browser-chromium/WebSocket:1", "browser-chromium/WebSocket:2",
            },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rows.All(r => r.Target.SkipReason == null)).IsTrue();
    }

    [Test]
    public async Task WithDataSource_CSharpTransports_AddCSharpRowsOnly()
    {
        var attribute = new ClientMatrixAttribute<int>(nameof(Sizes))
        {
            Languages = ["csharp", "node"],
            Transports = [ClientTransport.Tcp],
            CSharpTransports = [ClientTransport.Quic],
        };
        var rows = attribute.GetRows(attribute.GetValues(typeof(ClientMatrixTests)), transportOverride: null);

        await Assert.That(rows.Select(r => r.Target + ":" + r.Value)).IsEquivalentTo(
            new[] { "csharp/Tcp:1", "csharp/Tcp:2", "csharp/Quic:1", "csharp/Quic:2", "node/Tcp:1", "node/Tcp:2" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
            new[] { "csharp/Tcp", "csharp/WebSocketTls", "csharp/Quic", "node/Tcp", "node/WebSocketTls", "java/Tcp", "java/WebSocketTls", "java/WebSocketTls(okhttp)" },
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
        var plan = new ClientMatrixAttribute
        {
            Languages = ["csharp", "node", "java", "browser-chromium"],
            Transports = [ClientTransport.Quic],
        }.Plan(transportOverride: null, ClientLanguages.GetBuiltIn);

        var csharp = plan.Rows.Single();
        await Assert.That(csharp.Language).IsEqualTo("csharp");
        if (NexNet.InteropServer.InteropEndpointKinds.IsQuicSupported)
            await Assert.That(csharp.SkipReason).IsNull();
        else
            await Assert.That(csharp.SkipReason).Contains("msquic");
        await Assert.That(plan.NotApplicable.Select(n => n.Language)).IsEquivalentTo(
            new[] { "node", "java", "browser-chromium" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(plan.NotApplicable.All(n => n.Reason.Contains("no Quic transport", StringComparison.Ordinal))).IsTrue();
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
    [ClientMatrix(Languages = ["java"], Transports = [ClientTransport.WebSocket])]
    public async Task VariantRows_CarryTheVariantProperty(ClientTarget target)
    {
        var properties = TestContext.Current!.Metadata.TestDetails.CustomProperties;
        await Assert.That(properties[ClientMatrixAttribute.LanguageProperty]).IsEquivalentTo(new[] { "java" });
        if (target.Variant == null)
            await Assert.That(properties.ContainsKey(ClientMatrixAttribute.VariantProperty)).IsFalse();
        else
            await Assert.That(properties[ClientMatrixAttribute.VariantProperty]).IsEquivalentTo(new[] { ClientVariant.OkHttp });
    }
}
