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
/// A row whose language lacks the transport or a capability of <see cref="Requires"/> is still generated, with
/// <see cref="ClientTarget.SkipReason"/> set; <see cref="Harness.CreateClientAsync(ClientTarget, ClientSpec)"/> (or
/// <see cref="ClientTarget.SkipIfUnsupported"/>) then skips the test with that reason, so a gap is never silent. A
/// <see cref="ClientTransport.Quic"/> row is skipped the same way when the test process has no msquic.
/// </para>
/// <para>
/// A ported test keeps the C# coverage of its old <c>[Transports]</c> list with <see cref="CSharpTransports"/>: the
/// C#-only transports (QUIC) go there, so the other languages get no rows for them rather than a skip each.
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
///
/// // Every language over TCP and WSS, and C# over QUIC as well.
/// [Test, ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
/// public async Task Echo(ClientTarget target) { ... }
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
            if (language == ClientLanguages.CSharp && CSharpTransports.Length > 0)
                transports = transports.Concat(CSharpTransports).Distinct().ToArray();

            foreach (var transport in transports)
            {
                rows.Add(new ClientTarget(language, transport)
                {
                    SkipReason = info.GetSkipReason(transport, Requires) ?? transport.ToEndpointKind().GetUnsupportedReason(),
                });
            }
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
    public ValueTask OnTestDiscovered(DiscoveredTestContext context)
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
