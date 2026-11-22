using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NexNet.Interop;

/// <summary>
/// The coverage report of the client matrix: what each language (and variant) ran, passed, failed and skipped, the rows
/// that failed (with the first line of their message), and the combinations the matrix generated no row for, by reason. The matrix attributes record both: the combinations left out
/// when they build a test's rows, the results when a row ends. A test-session hook writes the report when
/// <see cref="ReportVariable"/> names a directory (<see cref="WriteRequestedReportAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// A skipped row is <em>cited</em> when its reason starts with a card id, a colon and a space (<c>K60: the Java client
/// ...</c>, <see cref="IsCited"/>): a known gap a card will close. In CI (<see cref="IsCiMode"/>) the hook fails the
/// session on any uncited skipped row, so a skip can never hide a gap there. Local runs may still skip for their
/// environment (no msquic, no symlinks).
/// </para>
/// <para>
/// The report is deterministic: rows, reasons and tests are sorted ordinally, and it holds no time or path (beyond what a
/// failed row's message says).
/// </para>
/// </remarks>
public static class MatrixCoverage
{
    /// <summary>The environment variable naming the directory the report is written to (unset: no report).</summary>
    public const string ReportVariable = "NEXNET_MATRIX_REPORT";

    /// <summary>The file name of the JSON report.</summary>
    public const string JsonFileName = "matrix-coverage.json";

    /// <summary>The file name of the Markdown report (the CI job's step summary).</summary>
    public const string MarkdownFileName = "matrix-coverage.md";

    private static readonly Regex CitedReason = new(@"^[A-Z]{1,2}-?[0-9]{1,3}[a-z]?: \S", RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<(string Test, NotApplicableRow Row), int> NotApplicable = new();
    private static readonly ConcurrentDictionary<string, RowResult> Results = new(StringComparer.Ordinal);

    /// <summary>Whether a skip reason cites a card: a card id, a colon and a space (<c>K60: ...</c>, <c>C-11: ...</c>).</summary>
    public static bool IsCited(string? reason) => reason != null && CitedReason.IsMatch(reason);

    /// <summary>Whether the process runs in CI: <c>CI=true</c> or <c>NEXNET_REQUIRE_SDKS=1</c>.</summary>
    public static bool IsCiMode()
        => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
           || Environment.GetEnvironmentVariable("NEXNET_REQUIRE_SDKS") == "1";

    /// <summary>The name of the test a data source builds rows for, as <c>Class.Method</c>.</summary>
    public static string TestName(DataGeneratorMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var method = metadata.TestInformation;
        return method == null ? "?" : method.Class.Type.Name + "." + method.Name;
    }

    /// <summary>Records the combinations of <paramref name="test"/> that get no row.</summary>
    /// <param name="test">The test, as <c>Class.Method</c>.</param>
    /// <param name="rows">The combinations.</param>
    /// <param name="rowsPerCombination">How many rows each combination would have had (the values of a data source).</param>
    public static void RecordNotApplicable(string test, IEnumerable<NotApplicableRow> rows, int rowsPerCombination = 1)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
            NotApplicable[(test, row)] = rowsPerCombination;
    }

    /// <summary>Records the result of a matrix row (a test that takes a <see cref="ClientTarget"/>).</summary>
    /// <param name="context">The test.</param>
    /// <param name="skipped">True when TUnit reports the test skipped.</param>
    public static ValueTask RecordResult(TestContext context, bool skipped)
    {
        ArgumentNullException.ThrowIfNull(context);
        var details = context.Metadata.TestDetails;
        var target = ClientMatrixAttribute.FindTarget(details.TestMethodArguments) ?? ClientMatrixAttribute.FindTarget(details.TestClassArguments);
        if (target == null)
            return default;

        var result = context.Execution.Result;
        var outcome = skipped ? RowOutcome.Skipped : result?.State switch
        {
            TestState.Passed => RowOutcome.Passed,
            TestState.Skipped => RowOutcome.Skipped,
            TestState.Failed or TestState.Timeout or TestState.Cancelled => RowOutcome.Failed,
            _ => RowOutcome.Other,
        };
        var reason = outcome == RowOutcome.Skipped
            ? context.Execution.SkipReason ?? result?.OverrideReason ?? result?.Exception?.Message ?? ""
            : null;
        var message = outcome == RowOutcome.Failed
            ? FirstLine(result?.Exception?.Message) ?? FirstLine(result?.OverrideReason) ?? result?.Exception?.GetType().Name ?? result?.State.ToString() ?? ""
            : null;
        var row = new RowResult(details.ClassType.Name + "." + details.MethodName, context.Metadata.DisplayName, target, outcome, reason, message);
        Results.AddOrUpdate(context.Id, row, (_, old) => old.Outcome == RowOutcome.Skipped && outcome != RowOutcome.Skipped ? old : row);
        return default;
    }

    /// <summary>The longest failure message the report keeps; a longer first line is cut and ends with "...".</summary>
    public const int MaxMessageLength = 300;

    /// <summary>
    /// The first non-blank line of <paramref name="message"/>, trimmed and cut to <see cref="MaxMessageLength"/>; null
    /// when there is none.
    /// </summary>
    public static string? FirstLine(string? message)
    {
        if (message == null)
            return null;
        foreach (var line in message.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty)
                continue;
            return trimmed.Length <= MaxMessageLength
                ? trimmed.ToString()
                : string.Concat(trimmed[..(MaxMessageLength - 3)], "...");
        }

        return null;
    }

    /// <summary>
    /// The report of what was recorded so far, for the languages that ran rows and the languages of
    /// <c>NEXNET_TEST_LANGS</c> other than C#.
    /// </summary>
    public static CoverageReport Build()
        => Build(
            Results.Values,
            NotApplicable.Select(n => (n.Key.Test, n.Key.Row, n.Value)),
            ClientLanguages.FromEnvironment().Where(l => l != ClientLanguages.CSharp));

    /// <summary>The report of <paramref name="results"/> and <paramref name="notApplicable"/> (one row per combination), for the languages that ran rows.</summary>
    public static CoverageReport Build(IEnumerable<RowResult> results, IEnumerable<(string Test, NotApplicableRow Row)> notApplicable)
        => Build(results, (notApplicable ?? throw new ArgumentNullException(nameof(notApplicable))).Select(n => (n.Test, n.Row, 1)), []);

    /// <summary>The report of <paramref name="results"/> and <paramref name="notApplicable"/>.</summary>
    /// <param name="results">The results of the rows that ran.</param>
    /// <param name="notApplicable">The combinations left out, with how many rows each would have had.</param>
    /// <param name="languages">Languages to report the combinations left out of even when they ran no row.</param>
    public static CoverageReport Build(
        IEnumerable<RowResult> results,
        IEnumerable<(string Test, NotApplicableRow Row, int Rows)> notApplicable,
        IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(notApplicable);
        ArgumentNullException.ThrowIfNull(languages);
        var rows = results.ToArray();
        var reported = rows.Select(r => r.Target.Language).Concat(languages).ToHashSet(StringComparer.Ordinal);

        var clients = rows
            .GroupBy(r => (r.Target.Language, r.Target.Variant))
            .Select(g => new ClientCoverage(
                g.Key.Language,
                g.Key.Variant,
                g.Count(),
                g.Count(r => r.Outcome == RowOutcome.Passed),
                g.Count(r => r.Outcome == RowOutcome.Failed),
                g.Count(r => r.Outcome == RowOutcome.Skipped),
                g.Count(r => r.Outcome == RowOutcome.Other)))
            .OrderBy(c => c.Language, StringComparer.Ordinal)
            .ThenBy(c => c.Variant ?? "", StringComparer.Ordinal)
            .ToArray();

        var missing = notApplicable
            .Where(n => reported.Contains(n.Row.Language))
            .GroupBy(n => (n.Row.Language, n.Row.Reason))
            .Select(g => new NotApplicableCoverage(g.Key.Language, g.Key.Reason, g.Sum(n => n.Rows), g.Select(n => n.Test).Distinct().Count()))
            .OrderBy(n => n.Language, StringComparer.Ordinal)
            .ThenBy(n => n.Reason, StringComparer.Ordinal)
            .ToArray();

        var skipped = rows
            .Where(r => r.Outcome == RowOutcome.Skipped)
            .Select(r => new SkippedRow(r.Test, r.DisplayName, r.Target.ToString(), r.SkipReason ?? "", IsCited(r.SkipReason)))
            .OrderBy(s => s.Test, StringComparer.Ordinal)
            .ThenBy(s => s.DisplayName, StringComparer.Ordinal)
            .ToArray();

        var failed = rows
            .Where(r => r.Outcome == RowOutcome.Failed)
            .Select(r => new FailedRow(r.Test, r.DisplayName, r.Target.ToString(), FirstLine(r.FailureMessage) ?? ""))
            .OrderBy(f => f.Test, StringComparer.Ordinal)
            .ThenBy(f => f.DisplayName, StringComparer.Ordinal)
            .ThenBy(f => f.Row, StringComparer.Ordinal)
            .ThenBy(f => f.Message, StringComparer.Ordinal)
            .ToArray();

        return new CoverageReport(clients, missing, skipped, failed);
    }

    /// <summary>
    /// Writes the report (<see cref="JsonFileName"/>, <see cref="MarkdownFileName"/>) to the directory of
    /// <see cref="ReportVariable"/>, if it is set; then, in CI (<see cref="IsCiMode"/>), fails when a row was skipped for
    /// a reason that cites no card.
    /// </summary>
    /// <exception cref="InvalidOperationException">In CI, a row was skipped for an uncited reason.</exception>
    public static async Task WriteRequestedReportAsync()
    {
        var directory = Environment.GetEnvironmentVariable(ReportVariable);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        var report = Build();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, JsonFileName), report.ToJson()).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, MarkdownFileName), report.ToMarkdown()).ConfigureAwait(false);

        var uncited = report.Skipped.Where(s => !s.Cited).ToArray();
        if (uncited.Length > 0 && IsCiMode())
        {
            throw new InvalidOperationException(
                uncited.Length + " matrix row(s) were skipped for a reason that cites no card (\"<card id>: ...\"); in CI every skip must cite one:"
                + Environment.NewLine + string.Join(Environment.NewLine, uncited.Select(s => "  " + s.DisplayName + " [" + s.Row + "]: " + s.Reason)));
        }
    }

    /// <summary>Clears what was recorded (tests of the report).</summary>
    internal static void Reset()
    {
        NotApplicable.Clear();
        Results.Clear();
    }
}

/// <summary>How a matrix row ended.</summary>
public enum RowOutcome
{
    /// <summary>Passed.</summary>
    Passed,

    /// <summary>Failed, timed out or was canceled.</summary>
    Failed,

    /// <summary>Skipped.</summary>
    Skipped,

    /// <summary>Anything else (not run).</summary>
    Other,
}

/// <summary>The result of one matrix row.</summary>
/// <param name="Test">The test, as <c>Class.Method</c>.</param>
/// <param name="DisplayName">The test's display name (with its arguments).</param>
/// <param name="Target">The row.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="SkipReason">Why it was skipped; null when it was not.</param>
/// <param name="FailureMessage">Why it failed (the exception's message; the report keeps its first line); null when it did not fail.</param>
public sealed record RowResult(
    string Test, string DisplayName, ClientTarget Target, RowOutcome Outcome, string? SkipReason, string? FailureMessage = null);

/// <summary>What one language (and variant) ran.</summary>
/// <param name="Language">The language.</param>
/// <param name="Variant">The variant (<see cref="ClientTarget.Variant"/>), or null.</param>
/// <param name="Run">The rows that ran or were skipped.</param>
/// <param name="Passed">The rows that passed.</param>
/// <param name="Failed">The rows that failed.</param>
/// <param name="Skipped">The rows that were skipped.</param>
/// <param name="Other">The rows that ended otherwise.</param>
public sealed record ClientCoverage(string Language, string? Variant, int Run, int Passed, int Failed, int Skipped, int Other)
{
    /// <summary>The client's label: <c>java</c>, <c>java (okhttp)</c>.</summary>
    public string Label => Variant == null ? Language : Language + " (" + Variant + ")";
}

/// <summary>The combinations of one language the matrix generated no row for, for one reason.</summary>
/// <param name="Language">The language.</param>
/// <param name="Reason">The reason.</param>
/// <param name="Rows">How many rows were not generated (a combination of a test and a transport is one row, or one per value of the test's data source).</param>
/// <param name="Tests">How many tests they belong to.</param>
public sealed record NotApplicableCoverage(string Language, string Reason, int Rows, int Tests);

/// <summary>A skipped row.</summary>
/// <param name="Test">The test, as <c>Class.Method</c>.</param>
/// <param name="DisplayName">The test's display name.</param>
/// <param name="Row">The row (<see cref="ClientTarget.ToString"/>).</param>
/// <param name="Reason">The skip reason.</param>
/// <param name="Cited">Whether the reason cites a card (<see cref="MatrixCoverage.IsCited"/>).</param>
public sealed record SkippedRow(string Test, string DisplayName, string Row, string Reason, bool Cited);

/// <summary>A failed row (failed, timed out or canceled).</summary>
/// <param name="Test">The test, as <c>Class.Method</c>.</param>
/// <param name="DisplayName">The test's display name.</param>
/// <param name="Row">The row (<see cref="ClientTarget.ToString"/>).</param>
/// <param name="Message">The first line of the failure message (<see cref="MatrixCoverage.FirstLine"/>).</param>
public sealed record FailedRow(string Test, string DisplayName, string Row, string Message);

/// <summary>The coverage report of a test run (<see cref="MatrixCoverage"/>).</summary>
/// <param name="Clients">What each language and variant ran.</param>
/// <param name="NotApplicable">The combinations left out, by language and reason.</param>
/// <param name="Skipped">The skipped rows.</param>
/// <param name="Failed">The failed rows.</param>
public sealed record CoverageReport(
    IReadOnlyList<ClientCoverage> Clients,
    IReadOnlyList<NotApplicableCoverage> NotApplicable,
    IReadOnlyList<SkippedRow> Skipped,
    IReadOnlyList<FailedRow> Failed)
{
    /// <summary>The JSON form (indented, LF line ends).</summary>
    public string ToJson()
    {
        var json = new JsonObject
        {
            ["clients"] = new JsonArray(Clients.Select(c => (JsonNode)new JsonObject
            {
                ["language"] = c.Language,
                ["variant"] = c.Variant,
                ["run"] = c.Run,
                ["passed"] = c.Passed,
                ["failed"] = c.Failed,
                ["skipped"] = c.Skipped,
                ["other"] = c.Other,
            }).ToArray()),
            ["notApplicable"] = new JsonArray(NotApplicable.Select(n => (JsonNode)new JsonObject
            {
                ["language"] = n.Language,
                ["reason"] = n.Reason,
                ["rows"] = n.Rows,
                ["tests"] = n.Tests,
            }).ToArray()),
            ["skipped"] = new JsonArray(Skipped.Select(s => (JsonNode)new JsonObject
            {
                ["test"] = s.Test,
                ["displayName"] = s.DisplayName,
                ["row"] = s.Row,
                ["reason"] = s.Reason,
                ["cited"] = s.Cited,
            }).ToArray()),
            ["failed"] = new JsonArray(Failed.Select(f => (JsonNode)new JsonObject
            {
                ["test"] = f.Test,
                ["displayName"] = f.DisplayName,
                ["row"] = f.Row,
                ["message"] = f.Message,
            }).ToArray()),
            ["uncitedSkips"] = Skipped.Count(s => !s.Cited),
        };
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>The Markdown form, for a GitHub step summary.</summary>
    public string ToMarkdown()
    {
        var md = new StringBuilder();
        md.Append("## Client matrix coverage\n\n");
        if (Clients.Count == 0)
        {
            md.Append("No matrix row ran.\n");
            return md.ToString();
        }

        md.Append("| Client | Rows run | Passed | Failed | Skipped | Other |\n|---|---:|---:|---:|---:|---:|\n");
        foreach (var c in Clients)
            md.Append($"| {Cell(c.Label)} | {c.Run} | {c.Passed} | {c.Failed} | {c.Skipped} | {c.Other} |\n");

        md.Append("\n### Failed rows\n\n");
        if (Failed.Count == 0)
        {
            md.Append("None.\n");
        }
        else
        {
            md.Append("| Test | Row | Message |\n|---|---|---|\n");
            foreach (var f in Failed)
                md.Append($"| {Cell(QualifiedName(f))} | {Cell(f.Row)} | {Cell(f.Message)} |\n");
        }

        md.Append("\n### Not applicable (no row generated)\n\n");
        if (NotApplicable.Count == 0)
        {
            md.Append("None.\n");
        }
        else
        {
            md.Append("| Client | Reason | Rows | Tests |\n|---|---|---:|---:|\n");
            foreach (var n in NotApplicable)
                md.Append($"| {Cell(n.Language)} | {Cell(n.Reason)} | {n.Rows} | {n.Tests} |\n");
        }

        md.Append("\n### Skipped rows\n\n");
        if (Skipped.Count == 0)
        {
            md.Append("None.\n");
        }
        else
        {
            md.Append("| Test | Row | Reason | Cites a card |\n|---|---|---|---|\n");
            foreach (var s in Skipped)
                md.Append($"| {Cell(s.DisplayName)} | {Cell(s.Row)} | {Cell(s.Reason)} | {(s.Cited ? "yes" : "**no**")} |\n");
        }

        return md.ToString();
    }

    // The display name with the test's class: Class.Method(args).
    private static string QualifiedName(FailedRow row)
    {
        var dot = row.Test.LastIndexOf('.');
        return dot < 0 ? row.DisplayName : row.Test[..(dot + 1)] + row.DisplayName;
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
