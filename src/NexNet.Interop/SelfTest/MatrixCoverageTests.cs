namespace NexNet.Interop.SelfTest;

public class MatrixCoverageTests
{
    [Test]
    [Arguments("K60: the Java client cannot do it yet", true)]
    [Arguments("C-11: harness op missing", true)]
    [Arguments("P01a: port pending", true)]
    [Arguments("H55: x", true)]
    [Arguments("QUIC (msquic) is not available on this platform.", false)]
    [Arguments("K60 the colon is missing", false)]
    [Arguments("k60: lower case", false)]
    [Arguments("K60:", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task IsCited_NeedsACardIdColonAndSpace(string? reason, bool cited)
    {
        await Assert.That(MatrixCoverage.IsCited(reason)).IsEqualTo(cited);
    }

    private static readonly RowResult[] Results =
    [
        new("Tests.B", "B(java/Tcp)", new ClientTarget("java", ClientTransport.Tcp), RowOutcome.Passed, null),
        new("Tests.A", "A(java/WebSocket)", new ClientTarget("java", ClientTransport.WebSocket), RowOutcome.Failed, null),
        new("Tests.A", "A(java/WebSocket(okhttp))", new ClientTarget("java", ClientTransport.WebSocket) { Variant = "okhttp" }, RowOutcome.Passed, null),
        new("Tests.C", "C(java/Tcp)", new ClientTarget("java", ClientTransport.Tcp), RowOutcome.Skipped, "no symlinks here"),
        new("Tests.D", "D(java/Tcp)", new ClientTarget("java", ClientTransport.Tcp), RowOutcome.Skipped, "K99: pending"),
    ];

    private static readonly (string Test, NotApplicableRow Row)[] Missing =
    [
        ("Tests.A", new NotApplicableRow("java", ClientTransport.Uds, "The java client has no Uds transport.")),
        ("Tests.B", new NotApplicableRow("java", ClientTransport.Uds, "The java client has no Uds transport.")),
        ("Tests.B", new NotApplicableRow("browser-chromium", ClientTransport.Tcp, "The browser-chromium client has no Tcp transport.")),
    ];

    [Test]
    public async Task Build_CountsEachClientAndVariant_AndTheNotApplicableRowsOfTheLanguagesThatRan()
    {
        var report = MatrixCoverage.Build(Results, Missing);

        await Assert.That(report.Clients).IsEquivalentTo(
            new[] { new ClientCoverage("java", null, 4, 1, 1, 2, 0), new ClientCoverage("java", "okhttp", 1, 1, 0, 0, 0) },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(report.Clients[1].Label).IsEqualTo("java (okhttp)");

        // browser-chromium ran no row, so its combinations are left out.
        await Assert.That(report.NotApplicable).IsEquivalentTo(new[] { new NotApplicableCoverage("java", "The java client has no Uds transport.", 2, 2) });

        await Assert.That(report.Skipped.Select(s => (s.Test, s.Cited))).IsEquivalentTo(
            new[] { ("Tests.C", false), ("Tests.D", true) }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Report_IsDeterministic_AndCountsUncitedSkips()
    {
        var report = MatrixCoverage.Build(Results, Missing);
        var shuffled = MatrixCoverage.Build(Results.Reverse(), Missing.Reverse());

        await Assert.That(shuffled.ToJson()).IsEqualTo(report.ToJson());
        await Assert.That(shuffled.ToMarkdown()).IsEqualTo(report.ToMarkdown());
        await Assert.That(report.ToJson()).Contains("\"uncitedSkips\": 1").And.DoesNotContain("\r");

        var markdown = report.ToMarkdown();
        await Assert.That(markdown).Contains("| java (okhttp) | 1 | 1 | 0 | 0 | 0 |");
        await Assert.That(markdown).Contains("| java | The java client has no Uds transport. | 2 | 2 |");
        await Assert.That(markdown).Contains("| C(java/Tcp) | java/Tcp | no symlinks here | **no** |");
    }

    [Test]
    public async Task EmptyRun_SaysSo()
    {
        var report = MatrixCoverage.Build([], Missing);
        await Assert.That(report.Clients).IsEmpty();
        await Assert.That(report.NotApplicable).IsEmpty();
        await Assert.That(report.ToMarkdown()).Contains("No matrix row ran.");
    }
}
