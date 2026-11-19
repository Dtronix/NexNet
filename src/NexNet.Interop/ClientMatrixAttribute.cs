namespace NexNet.Interop;

/// <summary>
/// TUnit data source of <see cref="ClientTarget"/> rows: every language of <c>NEXNET_TEST_LANGS</c>
/// (<see cref="ClientLanguages.FromEnvironment"/>; only <see cref="ClientLanguages.CSharp"/> when unset) times every
/// transport of <see cref="Transports"/>.
/// </summary>
/// <remarks>
/// A row whose language lacks the transport or a capability of <see cref="Requires"/> is still generated, with
/// <see cref="ClientTarget.SkipReason"/> set; <see cref="Harness.CreateClientAsync(ClientTarget, ClientSpec)"/> (or
/// <see cref="ClientTarget.SkipIfUnsupported"/>) then skips the test with that reason, so a gap is never silent.
/// </remarks>
/// <example>
/// <code>
/// [Test, ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocketTls], Requires = ClientCapabilities.FakeTime)]
/// public async Task Pings(ClientTarget target) { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class ClientMatrixAttribute : DataSourceGeneratorAttribute<ClientTarget>
{
    /// <summary>The transports; empty uses <see cref="ClientTransports.Default"/> (HttpSocketTls, WebSocketTls).</summary>
    public ClientTransport[] Transports { get; set; } = [];

    /// <summary>The capabilities the test needs.</summary>
    public ClientCapabilities Requires { get; set; }

    /// <summary>The languages; empty uses <see cref="ClientLanguages.FromEnvironment"/>.</summary>
    public string[] Languages { get; set; } = [];

    /// <summary>The rows, in language then transport order.</summary>
    public IReadOnlyList<ClientTarget> GetTargets()
    {
        var languages = Languages.Length > 0 ? (IReadOnlyList<string>)Languages : ClientLanguages.FromEnvironment();
        var transports = Transports.Length > 0 ? (IReadOnlyList<ClientTransport>)Transports : ClientTransports.Default;
        var rows = new List<ClientTarget>(languages.Count * transports.Count);
        foreach (var language in languages)
        {
            var info = ClientLanguages.Get(language);
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
}
