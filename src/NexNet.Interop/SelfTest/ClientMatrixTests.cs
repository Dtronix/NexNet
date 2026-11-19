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
        var rows = new ClientMatrixAttribute { Languages = ["csharp"] }.GetTargets();
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
