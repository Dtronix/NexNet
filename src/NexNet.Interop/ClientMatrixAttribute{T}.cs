using System.Collections;
using System.Reflection;
using TUnit.Core.Interfaces;

namespace NexNet.Interop;

/// <summary>
/// The rows of <see cref="ClientMatrixAttribute"/> times every value of a data source of the test class: each row
/// passes a <see cref="ClientTarget"/> and a <typeparamref name="T"/> (a size, a mode, ...).
/// </summary>
/// <remarks>
/// <para>
/// Use it instead of a <c>[MethodDataSource]</c> that builds <see cref="ClientMatrixAttribute"/> rows itself: those can
/// be built before the <c>LanguageHostPool</c> has registered what each host reports, so their skips would follow the
/// built-in capabilities. These rows, like those of <see cref="ClientMatrixAttribute"/>, are built when TUnit discovers
/// the test, after the pool of the class is initialized.
/// </para>
/// <para>
/// The data source is a static field, property or parameterless method of the test class (or a base class), of any
/// accessibility, holding or returning an <see cref="IEnumerable{T}"/> of <typeparamref name="T"/>. The rows are in
/// language, then transport, then data source order, and carry the same TUnit properties as
/// <see cref="ClientMatrixAttribute"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// private static readonly int[] Sizes = [1024, 1024 * 1024];
///
/// [Test, ClientMatrix&lt;int&gt;(nameof(Sizes), Transports = [ClientTransport.Tcp])]
/// public async Task RoundTrip(ClientTarget target, int size) { ... }
/// </code>
/// </example>
/// <typeparam name="T">The type of the extra dimension.</typeparam>
/// <param name="dataSource">The name of the static member of the test class that holds the values.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ClientMatrixAttribute<T>(string dataSource) : DataSourceGeneratorAttribute<ClientTarget, T>, ITestDiscoveryEventReceiver
{
    private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

    /// <summary>The name of the static member of the test class that holds the values.</summary>
    public string DataSource { get; } = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>The transports (<see cref="ClientMatrixAttribute.Transports"/>).</summary>
    public ClientTransport[] Transports { get; set; } = [];

    /// <summary>Transports added to the C# rows only (<see cref="ClientMatrixAttribute.CSharpTransports"/>).</summary>
    public ClientTransport[] CSharpTransports { get; set; } = [];

    /// <summary>The capabilities the test needs (<see cref="ClientMatrixAttribute.Requires"/>).</summary>
    public ClientCapabilities Requires { get; set; }

    /// <summary>The languages (<see cref="ClientMatrixAttribute.Languages"/>).</summary>
    public string[] Languages { get; set; } = [];

    /// <summary>
    /// The rows: every target of <see cref="ClientMatrixAttribute.GetTargets(string?)"/> times every value of
    /// <paramref name="values"/>, in that order.
    /// </summary>
    /// <param name="values">The values of the extra dimension.</param>
    /// <param name="transportOverride">A <c>NEXNET_TEST_TRANSPORTS</c> value (<see cref="ClientMatrixAttribute.GetTargets(string?)"/>).</param>
    public IReadOnlyList<(ClientTarget Target, T Value)> GetRows(IEnumerable<T> values, string? transportOverride)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.ToArray();
        var matrix = new ClientMatrixAttribute { Transports = Transports, CSharpTransports = CSharpTransports, Requires = Requires, Languages = Languages };
        return matrix.GetTargets(transportOverride).SelectMany(target => items.Select(value => (target, value))).ToArray();
    }

    /// <summary>The values of <see cref="DataSource"/> on <paramref name="testClass"/>.</summary>
    /// <exception cref="InvalidOperationException">No such static member, or it does not hold values of <typeparamref name="T"/>.</exception>
    public IEnumerable<T> GetValues(Type testClass)
    {
        ArgumentNullException.ThrowIfNull(testClass);
        object? source = null;
        var found = false;
        for (var type = testClass; type != null && !found; type = type.BaseType)
        {
            if (type.GetField(DataSource, StaticMembers) is { } field)
            {
                source = field.GetValue(null);
                found = true;
            }
            else if (type.GetProperty(DataSource, StaticMembers) is { GetMethod: not null } property)
            {
                source = property.GetValue(null);
                found = true;
            }
            else if (type.GetMethod(DataSource, StaticMembers, Type.EmptyTypes) is { } method)
            {
                source = method.Invoke(null, null);
                found = true;
            }
        }

        if (!found)
            throw new InvalidOperationException($"[ClientMatrix<{typeof(T).Name}>]: {testClass} has no static field, property or parameterless method '{DataSource}'.");

        return source switch
        {
            IEnumerable<T> typed => typed,
            IEnumerable untyped => untyped.Cast<T>(),
            _ => throw new InvalidOperationException($"[ClientMatrix<{typeof(T).Name}>]: {testClass}.{DataSource} holds no IEnumerable<{typeof(T).Name}>."),
        };
    }

    /// <inheritdoc />
    protected override IEnumerable<Func<(ClientTarget, T)>> GenerateDataSources(DataGeneratorMetadata dataGeneratorMetadata)
    {
        ArgumentNullException.ThrowIfNull(dataGeneratorMetadata);
        var testClass = dataGeneratorMetadata.TestInformation?.Class.Type
                        ?? throw new InvalidOperationException($"[ClientMatrix<{typeof(T).Name}>] has no test class to read '{DataSource}' from.");
        var values = GetValues(testClass);
        foreach (var row in GetRows(values, Environment.GetEnvironmentVariable(ClientTransports.EnvironmentVariable)))
            yield return () => row;
    }

    /// <summary>Tags the discovered row with its <see cref="ClientMatrixAttribute.LanguageProperty"/> and <see cref="ClientMatrixAttribute.TransportProperty"/>.</summary>
    /// <param name="context">The discovered test.</param>
    public ValueTask OnTestDiscovered(DiscoveredTestContext context) => ClientMatrixAttribute.TagDiscoveredRow(context);
}
