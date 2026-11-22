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
        new("Tests.A", "A(java/WebSocket)", new ClientTarget("java", ClientTransport.WebSocket), RowOutcome.Failed, null, "\n  Expected 1 | got 2\r\n  at Tests.A()"),
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
    public async Task FailedRows_AreNamed_WithTheFirstLineOfTheirMessage()
    {
        RowResult[] results =
        [
            .. Results,
            new("Tests.E", "E(node/Tcp)", new ClientTarget("node", ClientTransport.Tcp), RowOutcome.Failed, null, null),
            new("Tests.0", "0(node/Tcp)", new ClientTarget("node", ClientTransport.Tcp), RowOutcome.Failed, null, "Timed out"),
        ];
        var report = MatrixCoverage.Build(results, Missing);

        await Assert.That(report.Failed).IsEquivalentTo(
            new[]
            {
                new FailedRow("Tests.0", "0(node/Tcp)", "node/Tcp", "Timed out"),
                new FailedRow("Tests.A", "A(java/WebSocket)", "java/WebSocket", "Expected 1 | got 2"),
                new FailedRow("Tests.E", "E(node/Tcp)", "node/Tcp", ""),
            },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);

        var markdown = report.ToMarkdown();
        await Assert.That(markdown).Contains("### Failed rows\n\n| Test | Row | Message |\n|---|---|---|\n| Tests.0(node/Tcp) | node/Tcp | Timed out |\n");
        await Assert.That(markdown).Contains("| Tests.A(java/WebSocket) | java/WebSocket | Expected 1 \\| got 2 |");
        await Assert.That(markdown).DoesNotContain("at Tests.A()");

        var json = report.ToJson();
        await Assert.That(json).Contains("\"failed\": [");
        await Assert.That(json).Contains("\"message\": \"Expected 1 | got 2\"");
        await Assert.That(MatrixCoverage.Build(results.Reverse(), Missing).ToJson()).IsEqualTo(json);
    }

    [Test]
    public async Task NoFailedRow_SaysNone()
    {
        var report = MatrixCoverage.Build(Results.Where(r => r.Outcome != RowOutcome.Failed), Missing);
        await Assert.That(report.Failed).IsEmpty();
        await Assert.That(report.ToMarkdown()).Contains("### Failed rows\n\nNone.\n");
        await Assert.That(report.ToJson()).Contains("\"failed\": []");
    }

    [Test]
    [Arguments(null, null)]
    [Arguments("", null)]
    [Arguments(" \r\n\t\n", null)]
    [Arguments("one", "one")]
    [Arguments("\r\n  first  \nsecond", "first")]
    public async Task FirstLine_IsTheFirstNonBlankLineTrimmed(string? message, string? expected)
    {
        await Assert.That(MatrixCoverage.FirstLine(message)).IsEqualTo(expected);
    }

    [Test]
    public async Task FirstLine_CutsALongLine()
    {
        var line = MatrixCoverage.FirstLine(new string('x', 1000));
        await Assert.That(line!.Length).IsEqualTo(MatrixCoverage.MaxMessageLength);
        await Assert.That(line).EndsWith("...");
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
