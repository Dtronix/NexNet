using NexNet.InteropServer;
using TUnit.Core.Interfaces;

namespace NexNet.Interop;

/// <summary>
/// TUnit data source of <see cref="ClientTarget"/> rows: every language of <c>NEXNET_TEST_LANGS</c>
/// (<see cref="ClientLanguages.FromEnvironment"/>; only <see cref="ClientLanguages.CSharp"/> when unset) times every
/// transport of <see cref="Transports"/> (when it names none, <c>NEXNET_TEST_TRANSPORTS</c> decides:
/// <see cref="ClientTransports.Select(ClientLanguageInfo, string?)"/>), plus the
/// <see cref="ClientLanguages.CSharp"/> rows of <see cref="CSharpTransports"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only applicable rows are generated: a language that lacks the transport or a capability of <see cref="Requires"/>
/// (<see cref="ClientLanguageInfo.GetNotApplicableReason"/>, from <see cref="ClientLanguages"/> and what each host reports)
/// gets no row for it. The combinations left out are recorded with their reason for the coverage report
/// (<see cref="MatrixCoverage"/>), and the parity audit (<c>ClientParityTests</c>) reads the same table, so a test that
/// runs on no row of a language is a documented exception. A language that gains a transport or capability gets its rows
/// from the same table.
/// </para>
/// <para>
/// A language's variants (<see cref="ClientLanguageInfo.Variants"/>: the Java client on OkHttp) add a row after each
/// row of a transport they implement, with <see cref="ClientTarget.Variant"/> set (<c>java/WebSocket(okhttp)</c>).
/// </para>
/// <para>
/// A <see cref="ClientTransport.Quic"/> row is still generated when the test process has no msquic, with
/// <see cref="ClientTarget.SkipReason"/> set; <see cref="Harness.CreateClientAsync(ClientTarget, ClientSpec)"/> (or
/// <see cref="ClientTarget.SkipIfUnsupported"/>) then skips it visibly. That is an environment skip; in CI every skipped
/// row must cite a card (<see cref="MatrixCoverage.IsCited"/>).
/// </para>
/// <para>
/// A ported test keeps the C# coverage of its old <c>[Transports]</c> list with <see cref="CSharpTransports"/>: the
/// C#-only transports (QUIC) go there. A test of C#-only API by design sets <see cref="Languages"/> to
/// <see cref="ClientLanguages.CSharp"/> and is a parity exception.
/// </para>
/// <para>
/// Every row carries the TUnit properties <see cref="LanguageProperty"/> (<c>Lang</c>) and <see cref="TransportProperty"/>
/// (<c>Transport</c>), and a variant row <see cref="VariantProperty"/> (<c>Variant</c>), so a run can select one
/// language's rows: <c>dotnet test ... --treenode-filter "/*/*/*/*[Lang=node]"</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Test, ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocketTls], Requires = ClientCapabilities.FakeTime)]
/// public async Task Pings(ClientTarget target) { ... }
///
/// // Every language over TCP and WSS, and C# over QUIC as well.
/// [Test, ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
/// public async Task Echo(ClientTarget target) { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class ClientMatrixAttribute : DataSourceGeneratorAttribute<ClientTarget>, ITestDiscoveryEventReceiver,
    ITestEndEventReceiver, ITestSkippedEventReceiver
{
    /// <summary>The TUnit property holding a row's language (<see cref="ClientTarget.Language"/>).</summary>
    public const string LanguageProperty = "Lang";

    /// <summary>The TUnit property holding a row's transport (<see cref="ClientTarget.Transport"/>).</summary>
    public const string TransportProperty = "Transport";

    /// <summary>The TUnit property holding a variant row's variant (<see cref="ClientTarget.Variant"/>); absent on other rows.</summary>
    public const string VariantProperty = "Variant";

    /// <summary>
    /// The transports; empty uses <c>NEXNET_TEST_TRANSPORTS</c>, or <see cref="ClientTransports.Default"/>
    /// (HttpSocketTls, WebSocketTls) when it is unset.
    /// </summary>
    public ClientTransport[] Transports { get; set; } = [];

    /// <summary>
    /// Transports added to the <see cref="ClientLanguages.CSharp"/> rows only, after those of <see cref="Transports"/>
    /// (or of <c>NEXNET_TEST_TRANSPORTS</c>); a transport already there is not repeated. Other languages get no rows
    /// for them. For the C#-only transports (<see cref="ClientTransport.Quic"/>) a test covered before its port.
    /// </summary>
    public ClientTransport[] CSharpTransports { get; set; } = [];

    /// <summary>The capabilities the test needs.</summary>
    public ClientCapabilities Requires { get; set; }

    /// <summary>The languages; empty uses <see cref="ClientLanguages.FromEnvironment"/>.</summary>
    public string[] Languages { get; set; } = [];

    /// <summary>
    /// The applicable rows, in language then transport order (a variant row after the row it varies), with the
    /// <c>NEXNET_TEST_TRANSPORTS</c> of the process.
    /// </summary>
    public IReadOnlyList<ClientTarget> GetTargets()
        => GetTargets(Environment.GetEnvironmentVariable(ClientTransports.EnvironmentVariable));

    /// <summary>The applicable rows, in language then transport order (a variant row after the row it varies).</summary>
    /// <param name="transportOverride">
    /// A <c>NEXNET_TEST_TRANSPORTS</c> value, used when <see cref="Transports"/> is empty
    /// (<see cref="ClientTransports.Select(ClientLanguageInfo, string?)"/>).
    /// </param>
    public IReadOnlyList<ClientTarget> GetTargets(string? transportOverride) => Plan(transportOverride).Rows;

    /// <summary>
    /// The applicable rows and the combinations left out (<see cref="MatrixPlan"/>).
    /// </summary>
    /// <param name="transportOverride">
    /// A <c>NEXNET_TEST_TRANSPORTS</c> value, used when <see cref="Transports"/> is empty
    /// (<see cref="ClientTransports.Select(ClientLanguageInfo, string?)"/>).
    /// </param>
    /// <param name="languageInfo">
    /// What each language supports; default <see cref="ClientLanguages.Get"/> (what its host reported, else the built-in
    /// table). The parity audit passes <see cref="ClientLanguages.GetBuiltIn"/>.
    /// </param>
    public MatrixPlan Plan(string? transportOverride, Func<string, ClientLanguageInfo>? languageInfo = null)
    {
        languageInfo ??= ClientLanguages.Get;
        var languages = Languages.Length > 0 ? (IReadOnlyList<string>)Languages : ClientLanguages.FromEnvironment();
        var rows = new List<ClientTarget>();
        var notApplicable = new List<NotApplicableRow>();
        foreach (var language in languages)
        {
            var info = languageInfo(language);
            var transports = Transports.Length > 0
                ? (IReadOnlyList<ClientTransport>)Transports
                : ClientTransports.Select(info, transportOverride);
            if (language == ClientLanguages.CSharp && CSharpTransports.Length > 0)
                transports = transports.Concat(CSharpTransports).Distinct().ToArray();

            foreach (var transport in transports)
            {
                if (info.GetNotApplicableReason(transport, Requires) is { } reason)
                {
                    notApplicable.Add(new NotApplicableRow(language, transport, reason));
                    continue;
                }

                var skip = transport.ToEndpointKind().GetUnsupportedReason();
                rows.Add(new ClientTarget(language, transport) { SkipReason = skip });
                foreach (var variant in info.Variants)
                {
                    if (variant.Transports.Contains(transport))
                        rows.Add(new ClientTarget(language, transport) { Variant = variant.Name, SkipReason = skip });
                }
            }
        }

        return new MatrixPlan(rows, notApplicable);
    }

    /// <summary>
    /// The plan of the test process (<c>NEXNET_TEST_TRANSPORTS</c>), with its combinations left out recorded for the
    /// coverage report of the test <paramref name="dataGeneratorMetadata"/> builds rows for. A language of
    /// <c>NEXNET_TEST_LANGS</c> that <see cref="Languages"/> leaves out (a C#-only test by design) is recorded too.
    /// </summary>
    /// <param name="dataGeneratorMetadata">The test's data source metadata.</param>
    /// <param name="rowsPerCombination">How many rows each combination makes (the values of a data source).</param>
    public MatrixPlan PlanAndRecord(DataGeneratorMetadata dataGeneratorMetadata, int rowsPerCombination = 1)
    {
        var plan = Plan(Environment.GetEnvironmentVariable(ClientTransports.EnvironmentVariable));
        var test = MatrixCoverage.TestName(dataGeneratorMetadata);
        MatrixCoverage.RecordNotApplicable(test, plan.NotApplicable, rowsPerCombination);
        if (Languages.Length > 0)
        {
            var reason = "The test runs on " + string.Join(", ", Languages) + " only (its Languages; a documented parity exception).";
            var transports = Transports.Length > 0 ? Transports : ClientTransports.Default.ToArray();
            MatrixCoverage.RecordNotApplicable(test, ClientLanguages.FromEnvironment()
                .Where(l => !Languages.Contains(l))
                .SelectMany(l => transports.Select(t => new NotApplicableRow(l, t, reason))), rowsPerCombination);
        }

        return plan;
    }

    /// <inheritdoc />
    protected override IEnumerable<Func<ClientTarget>> GenerateDataSources(DataGeneratorMetadata dataGeneratorMetadata)
    {
        foreach (var row in PlanAndRecord(dataGeneratorMetadata).Rows)
            yield return () => row;
    }

    /// <summary>Tags the discovered row with its <see cref="LanguageProperty"/> and <see cref="TransportProperty"/>.</summary>
    /// <param name="context">The discovered test.</param>
    public ValueTask OnTestDiscovered(DiscoveredTestContext context) => TagDiscoveredRow(context);

    /// <summary>Records the row's result for the coverage report (<see cref="MatrixCoverage"/>).</summary>
    /// <param name="context">The test that ended.</param>
    public ValueTask OnTestEnd(TestContext context) => MatrixCoverage.RecordResult(context, skipped: false);

    /// <summary>Records the skipped row for the coverage report (<see cref="MatrixCoverage"/>).</summary>
    /// <param name="context">The skipped test.</param>
    public ValueTask OnTestSkipped(TestContext context) => MatrixCoverage.RecordResult(context, skipped: true);

    /// <summary>
    /// Adds the <see cref="LanguageProperty"/>, <see cref="TransportProperty"/> and (on a variant row)
    /// <see cref="VariantProperty"/> of the row's target.
    /// </summary>
    public static ValueTask TagDiscoveredRow(DiscoveredTestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = FindTarget(context.TestDetails.TestMethodArguments) ?? FindTarget(context.TestDetails.TestClassArguments);
        if (target != null)
        {
            context.AddProperty(LanguageProperty, target.Language);
            context.AddProperty(TransportProperty, target.Transport.ToString());
            if (target.Variant != null)
                context.AddProperty(VariantProperty, target.Variant);
        }

        return default;
    }

    /// <summary>The <see cref="ClientTarget"/> among a test's arguments, if any.</summary>
    internal static ClientTarget? FindTarget(object?[]? arguments)
    {
        if (arguments == null)
            return null;

        foreach (var argument in arguments)
        {
            if (argument is ClientTarget target)
                return target;
        }

        return null;
    }
}

/// <summary>A combination of a matrix test the matrix generates no row for, and why.</summary>
/// <param name="Language">The language.</param>
/// <param name="Transport">The transport.</param>
/// <param name="Reason">Why it does not apply (<see cref="ClientLanguageInfo.GetNotApplicableReason"/>).</param>
public sealed record NotApplicableRow(string Language, ClientTransport Transport, string Reason);

/// <summary>The rows of a matrix test and the combinations left out (<see cref="ClientMatrixAttribute.Plan"/>).</summary>
/// <param name="Rows">The applicable rows.</param>
/// <param name="NotApplicable">The combinations of the test's languages and transports that do not apply.</param>
public sealed record MatrixPlan(IReadOnlyList<ClientTarget> Rows, IReadOnlyList<NotApplicableRow> NotApplicable);
