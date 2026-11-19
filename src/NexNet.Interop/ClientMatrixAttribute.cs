using TUnit.Core.Interfaces;

namespace NexNet.Interop;

/// <summary>
/// TUnit data source of <see cref="ClientTarget"/> rows: every language of <c>NEXNET_TEST_LANGS</c>
/// (<see cref="ClientLanguages.FromEnvironment"/>; only <see cref="ClientLanguages.CSharp"/> when unset) times every
/// transport of <see cref="Transports"/> (when it names none, <c>NEXNET_TEST_TRANSPORTS</c> decides:
/// <see cref="ClientTransports.Select(ClientLanguageInfo, string?)"/>).
/// </summary>
/// <remarks>
/// <para>
/// A row whose language lacks the transport or a capability of <see cref="Requires"/> is still generated, with
/// <see cref="ClientTarget.SkipReason"/> set; <see cref="Harness.CreateClientAsync(ClientTarget, ClientSpec)"/> (or
/// <see cref="ClientTarget.SkipIfUnsupported"/>) then skips the test with that reason, so a gap is never silent.
/// </para>
/// <para>
/// Every row carries the TUnit properties <see cref="LanguageProperty"/> (<c>Lang</c>) and <see cref="TransportProperty"/>
/// (<c>Transport</c>), so a run can select one language's rows:
/// <c>dotnet test ... --treenode-filter "/*/*/*/*[Lang=node]"</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Test, ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocketTls], Requires = ClientCapabilities.FakeTime)]
/// public async Task Pings(ClientTarget target) { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class ClientMatrixAttribute : DataSourceGeneratorAttribute<ClientTarget>, ITestDiscoveryEventReceiver
{
    /// <summary>The TUnit property holding a row's language (<see cref="ClientTarget.Language"/>).</summary>
    public const string LanguageProperty = "Lang";

    /// <summary>The TUnit property holding a row's transport (<see cref="ClientTarget.Transport"/>).</summary>
    public const string TransportProperty = "Transport";

    /// <summary>
    /// The transports; empty uses <c>NEXNET_TEST_TRANSPORTS</c>, or <see cref="ClientTransports.Default"/>
    /// (HttpSocketTls, WebSocketTls) when it is unset.
    /// </summary>
    public ClientTransport[] Transports { get; set; } = [];

    /// <summary>The capabilities the test needs.</summary>
    public ClientCapabilities Requires { get; set; }

    /// <summary>The languages; empty uses <see cref="ClientLanguages.FromEnvironment"/>.</summary>
    public string[] Languages { get; set; } = [];

    /// <summary>The rows, in language then transport order, with the <c>NEXNET_TEST_TRANSPORTS</c> of the process.</summary>
    public IReadOnlyList<ClientTarget> GetTargets()
        => GetTargets(Environment.GetEnvironmentVariable(ClientTransports.EnvironmentVariable));

    /// <summary>The rows, in language then transport order.</summary>
    /// <param name="transportOverride">
    /// A <c>NEXNET_TEST_TRANSPORTS</c> value, used when <see cref="Transports"/> is empty
    /// (<see cref="ClientTransports.Select(ClientLanguageInfo, string?)"/>).
    /// </param>
    public IReadOnlyList<ClientTarget> GetTargets(string? transportOverride)
    {
        var languages = Languages.Length > 0 ? (IReadOnlyList<string>)Languages : ClientLanguages.FromEnvironment();
        var rows = new List<ClientTarget>();
        foreach (var language in languages)
        {
            var info = ClientLanguages.Get(language);
            var transports = Transports.Length > 0
                ? (IReadOnlyList<ClientTransport>)Transports
                : ClientTransports.Select(info, transportOverride);
            foreach (var transport in transports)
                rows.Add(new ClientTarget(language, transport) { SkipReason = info.GetSkipReason(transport, Requires) });
        }

        return rows;
    }

    /// <inheritdoc />
    protected override IEnumerable<Func<ClientTarget>> GenerateDataSources(DataGeneratorMetadata dataGeneratorMetadata)
    {
        foreach (var row in GetTargets())
            yield return () => row;
    }

    /// <summary>Tags the discovered row with its <see cref="LanguageProperty"/> and <see cref="TransportProperty"/>.</summary>
    /// <param name="context">The discovered test.</param>
    public ValueTask OnTestDiscovered(DiscoveredTestContext context) => TagDiscoveredRow(context);

    /// <summary>Adds the <see cref="LanguageProperty"/> and <see cref="TransportProperty"/> of the row's target.</summary>
    internal static ValueTask TagDiscoveredRow(DiscoveredTestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = FindTarget(context.TestDetails.TestMethodArguments) ?? FindTarget(context.TestDetails.TestClassArguments);
        if (target != null)
        {
            context.AddProperty(LanguageProperty, target.Language);
            context.AddProperty(TransportProperty, target.Transport.ToString());
        }

        return default;
    }

    private static ClientTarget? FindTarget(object?[]? arguments)
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
