namespace NexNet.IntegrationTests;

/// <summary>
/// Runs a test once per transport (<see cref="BaseTests.Type"/>), passing the transport as the test's first argument.
/// With no arguments every transport runs; pass transports to run only those, or set <see cref="Except"/> to run every
/// transport but those.
/// </summary>
/// <example>
/// <code>
/// [Test]
/// [Transports]                                            // every transport
/// [Transports(Type.WebSocket, Type.HttpSocket)]           // only these
/// [Transports(Except = new[] { Type.Uds })]               // every transport but these
/// public async Task Test(Type type) { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class TransportsAttribute : DataSourceGeneratorAttribute<BaseTests.Type>
{
    private static readonly BaseTests.Type[] All =
    [
        BaseTests.Type.Uds,
        BaseTests.Type.Tcp,
        BaseTests.Type.TcpTls,
        BaseTests.Type.Quic,
        BaseTests.Type.WebSocket,
        BaseTests.Type.HttpSocket,
    ];

    private readonly BaseTests.Type[] _only;

    /// <summary>Runs the test once per transport in <paramref name="only"/>, or once per transport when it is empty.</summary>
    public TransportsAttribute(params BaseTests.Type[] only)
    {
        _only = only;
    }

    /// <summary>Transports to leave out.</summary>
    public BaseTests.Type[] Except { get; set; } = [];

    /// <summary>The transports this attribute runs, in declaration order of <see cref="BaseTests.Type"/>.</summary>
    public IEnumerable<BaseTests.Type> Selected =>
        All.Where(t => (_only.Length == 0 || _only.Contains(t)) && !Except.Contains(t));

    protected override IEnumerable<Func<BaseTests.Type>> GenerateDataSources(DataGeneratorMetadata dataGeneratorMetadata)
    {
        foreach (var type in Selected)
            yield return () => type;
    }
}
