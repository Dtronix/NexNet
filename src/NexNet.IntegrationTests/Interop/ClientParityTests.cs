using System.Reflection;
using System.Reflection.Emit;
using NexNet.Interop;

namespace NexNet.IntegrationTests.Interop;

/// <summary>
/// The parity audit of the client matrix: every test on the matrix (<see cref="ClientMatrixAttribute"/>,
/// <see cref="ClientMatrixAttribute{T}"/>, <see cref="CrossLanguageSmokeAttribute"/>) has a runnable row for every
/// client language and checks the same thing on each, unless it is on the documented exception list
/// (<see cref="ClientParityExceptions"/>). A parity gap therefore cannot appear, or stay after it is fixed, silently.
/// </summary>
/// <remarks>
/// <para>
/// The audit reads the attributes, not <c>NEXNET_TEST_LANGS</c>: it builds each language's rows with
/// <see cref="ClientMatrixAttribute.GetTargets(string?)"/> and the default transports, as PR CI does. A language has
/// no runnable row when every row has a <see cref="ClientTarget.SkipReason"/> (transport or capability), when the
/// attribute's <c>Languages</c> leaves it out, or when the test skips other languages at run time
/// (<c>MatrixTestBase.RequireInProc</c>).
/// </para>
/// <para>
/// A test that runs on a language can still check part of its behavior on the C# client only: a branch on
/// <see cref="ClientLanguages.CSharp"/>, <c>MatrixTestBase.InProc</c> or <c>MatrixTestBase.AssertNotOutstandingInProc</c>.
/// The audit finds those in the IL of the test (its async state machine, lambdas and local functions included), and
/// they need an exception of their own (<see cref="ParityGap.CSharpOnlyAssertion"/>).
/// </para>
/// </remarks>
internal class ClientParityTests
{
    /// <summary>The foreign client languages the audit checks, by <see cref="ParityLanguages"/> group.</summary>
    private static readonly (string Language, ParityLanguages Group)[] Languages =
    [
        (ClientLanguages.Java, ParityLanguages.Java),
        (ClientLanguages.Node, ParityLanguages.Node),
        (ClientLanguages.BrowserChromium, ParityLanguages.Browser),
        (ClientLanguages.BrowserFirefox, ParityLanguages.Browser),
        (ClientLanguages.BrowserWebKit, ParityLanguages.Browser),
    ];

    [Test]
    public async Task EveryMatrixTest_RunsOnEveryLanguage_OrIsADocumentedException()
    {
        var issues = Audit(ClientParityExceptions.All);
        if (issues.Count > 0)
            Assert.Fail(issues.Count + " parity issue(s):" + Environment.NewLine + string.Join(Environment.NewLine, issues));
        await Task.CompletedTask;
    }

    [Test]
    public async Task RemovingAnyException_FailsTheAudit()
    {
        var notNeeded = ClientParityExceptions.All
            .Where(entry => Audit(ClientParityExceptions.All.Where(e => !ReferenceEquals(e, entry)).ToArray()).Count == 0)
            .Select(entry => entry.ToString());
        var list = string.Join(Environment.NewLine, notNeeded);
        if (list.Length > 0)
            Assert.Fail("The audit still passes without these exceptions:" + Environment.NewLine + list);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Exceptions_HaveAReason_AndAreListedOnce()
    {
        var entries = ClientParityExceptions.All;
        await Assert.That(entries.Where(e => string.IsNullOrWhiteSpace(e.Reason) || e.Languages == ParityLanguages.None)).IsEmpty();
        await Assert.That(entries.GroupBy(e => (e.Test, e.Gap)).Where(g => g.Count() > 1).Select(g => g.Key.Test)).IsEmpty();
    }

    [Test]
    public async Task Audit_FindsAMissingRow()
    {
        // A test the exceptions do not cover is reported, with its class, method and languages.
        var issues = Audit(ClientParityExceptions.All.Where(e => e.Test != ClientParityExceptions.Probe).ToArray());
        await Assert.That(issues).Count().IsEqualTo(1);
        await Assert.That(issues[0]).Contains(ClientParityExceptions.Probe).And.Contains("browser");
    }

    /// <summary>The audit: what is missing from <paramref name="exceptions"/>, and what in it is stale.</summary>
    internal static IReadOnlyList<string> Audit(IReadOnlyList<ParityException> exceptions)
    {
        var issues = new List<string>();
        var gaps = FindGaps(issues);

        foreach (var ((test, gap), languages) in gaps.OrderBy(g => g.Key.Test, StringComparer.Ordinal).ThenBy(g => g.Key.Gap))
        {
            var covered = exceptions.Where(e => e.Test == test && e.Gap == gap).Aggregate(ParityLanguages.None, (all, e) => all | e.Languages);
            var missing = languages & ~covered;
            if (missing != ParityLanguages.None)
                issues.Add($"{test}: {Describe(gap)} on {Names(missing)}, and it is not on the exception list (ClientParityExceptions).");
        }

        foreach (var entry in exceptions)
        {
            var actual = gaps.GetValueOrDefault((entry.Test, entry.Gap));
            var stale = entry.Languages & ~actual;
            if (stale != ParityLanguages.None)
                issues.Add($"{entry.Test}: the exception ({entry.Gap}) on {Names(stale)} is stale: no such gap. Remove it from ClientParityExceptions.");
        }

        return issues;
    }

    private static string Describe(ParityGap gap) => gap switch
    {
        ParityGap.NoRunnableRow => "no runnable row",
        ParityGap.CSharpOnlyAssertion => "an assertion runs on the C# client only",
        _ => gap.ToString(),
    };

    private static string Names(ParityLanguages languages)
        => string.Join(", ", new[] { ParityLanguages.Java, ParityLanguages.Node, ParityLanguages.Browser }
            .Where(l => languages.HasFlag(l))
            .Select(l => l.ToString().ToLowerInvariant()));

    /// <summary>Every gap of every matrix test, by test (<c>Class.Method</c>) and kind.</summary>
    private static Dictionary<(string Test, ParityGap Gap), ParityLanguages> FindGaps(List<string> issues)
    {
        var gaps = new Dictionary<(string, ParityGap), ParityLanguages>();
        var calls = MatrixHelpers();

        foreach (var type in typeof(ClientParityTests).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<TestAttribute>() == null)
                    continue;

                var test = type.Name + "." + method.Name;
                var source = MatrixSource.Find(method, type);
                if (source == null)
                {
                    if (method.GetParameters().Any(p => p.ParameterType == typeof(ClientTarget)))
                        issues.Add($"{test}: takes a ClientTarget from a data source the audit does not know; use [ClientMatrix] or [CrossLanguageSmoke].");
                    continue;
                }

                var body = ScanBodies(method, calls);
                var noRow = ParityLanguages.None;
                foreach (var (language, group) in Languages)
                {
                    if (body.RequiresInProc || !source.HasRunnableRow(language))
                        noRow |= group;
                }

                if (noRow != ParityLanguages.None)
                    gaps[(test, ParityGap.NoRunnableRow)] = noRow;

                var runs = ParityLanguages.All & ~noRow;
                if (body.HasCSharpOnlyAssertion && runs != ParityLanguages.None)
                    gaps[(test, ParityGap.CSharpOnlyAssertion)] = runs;
            }
        }

        return gaps;
    }

    // ----- The rows of a test -----

    /// <summary>The matrix attribute of a test, as the settings of a <see cref="ClientMatrixAttribute"/>.</summary>
    private sealed class MatrixSource
    {
        public ClientTransport[] Transports { get; init; } = [];
        public ClientTransport[] CSharpTransports { get; init; } = [];
        public ClientCapabilities Requires { get; init; }
        public string[] Languages { get; init; } = [];

        public static MatrixSource? Find(MethodInfo method, Type type)
        {
            if ((method.GetCustomAttribute<ClientMatrixAttribute>() ?? type.GetCustomAttribute<ClientMatrixAttribute>()) is { } matrix)
            {
                return new MatrixSource { Transports = matrix.Transports, CSharpTransports = matrix.CSharpTransports, Requires = matrix.Requires, Languages = matrix.Languages };
            }

            if (method.GetCustomAttribute<CrossLanguageSmokeAttribute>() is { } smoke)
                return new MatrixSource { Transports = smoke.Transports, Requires = smoke.Requires };

            var generic = method.GetCustomAttributes().FirstOrDefault(a => a.GetType().IsGenericType && a.GetType().GetGenericTypeDefinition() == typeof(ClientMatrixAttribute<>));
            if (generic == null)
                return null;

            T Get<T>(string name) => (T)generic.GetType().GetProperty(name)!.GetValue(generic)!;
            return new MatrixSource
            {
                Transports = Get<ClientTransport[]>(nameof(Transports)),
                CSharpTransports = Get<ClientTransport[]>(nameof(CSharpTransports)),
                Requires = Get<ClientCapabilities>(nameof(Requires)),
                Languages = Get<string[]>(nameof(Languages)),
            };
        }

        /// <summary>Whether <paramref name="language"/> gets a row without a skip reason (default transports, as on PR CI).</summary>
        public bool HasRunnableRow(string language)
        {
            if (Languages.Length > 0 && !Languages.Contains(language))
                return false;

            var rows = new ClientMatrixAttribute { Transports = Transports, CSharpTransports = CSharpTransports, Requires = Requires, Languages = [language] }
                .GetTargets(transportOverride: null);
            return rows.Any(r => r.Language == language && r.SkipReason == null);
        }
    }

    // ----- The IL of a test -----

    private sealed record Helpers(int RequireInProc, int[] CSharpOnly);

    private readonly record struct BodyScan(bool RequiresInProc, bool HasCSharpOnlyAssertion);

    /// <summary>The metadata tokens of the <c>MatrixTestBase</c> helpers the audit looks for.</summary>
    private static Helpers MatrixHelpers()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        int Token(string name) => (typeof(MatrixTestBase).GetMethod(name, flags)
                                   ?? throw new InvalidOperationException($"MatrixTestBase.{name} not found: update the parity audit.")).MetadataToken;
        return new Helpers(Token("RequireInProc"), [Token("InProc"), Token("AssertNotOutstandingInProc")]);
    }

    /// <summary>What the IL of the test, its state machine, lambdas and local functions calls.</summary>
    private static BodyScan ScanBodies(MethodInfo test, Helpers helpers)
    {
        var requiresInProc = false;
        var csharpOnly = false;
        foreach (var body in Bodies(test))
        {
            // The operand index of the last ldstr "csharp": a string comparison right after it (with at most the
            // ClientTarget.Language getter between) is a branch on the language; the name alone (a client named
            // "csharp") is not.
            var csharpAt = -3;
            var operands = Operands(body);
            for (var i = 0; i < operands.Count; i++)
            {
                var (operand, token) = operands[i];
                if (operand == OperandType.InlineString)
                {
                    if (body.Module.ResolveString(token) == ClientLanguages.CSharp)
                        csharpAt = i;
                }
                else if (token == helpers.RequireInProc)
                {
                    requiresInProc = true;
                }
                else if (helpers.CSharpOnly.Contains(token) || (i - csharpAt <= 2 && IsStringComparison(body.Module, token)))
                {
                    csharpOnly = true;
                }
            }
        }

        return new BodyScan(requiresInProc, csharpOnly);
    }

    private static bool IsStringComparison(Module module, int token)
    {
        try
        {
            return module.ResolveMethod(token) is { DeclaringType: var type, Name: "op_Equality" or "op_Inequality" or "Equals" } && type == typeof(string);
        }
        catch (ArgumentException)
        {
            // A generic member reference: not a string comparison.
            return false;
        }
    }

    /// <summary>The test method and the compiler-generated methods of its body (named <c>&lt;Test&gt;...</c>).</summary>
    private static IEnumerable<MethodBase> Bodies(MethodInfo test)
    {
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var marker = "<" + test.Name + ">";
        var declaring = test.DeclaringType!;
        yield return test;

        foreach (var method in declaring.GetMethods(all).Where(m => m.Name.Contains(marker, StringComparison.Ordinal)))
            yield return method;

        foreach (var nested in Nested(declaring))
        {
            var whole = nested.Name.Contains(marker, StringComparison.Ordinal);
            foreach (var method in nested.GetMethods(all).Where(m => whole || m.Name.Contains(marker, StringComparison.Ordinal)))
                yield return method;
        }
    }

    private static IEnumerable<Type> Nested(Type type)
    {
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            yield return nested;
            foreach (var inner in Nested(nested))
                yield return inner;
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .DistinctBy(o => o.Value)
        .ToDictionary(o => o.Value);

    /// <summary>The method and string operands of the IL of <paramref name="method"/>.</summary>
    private static List<(OperandType Operand, int Token)> Operands(MethodBase method)
    {
        var result = new List<(OperandType, int)>();
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
            return result;

        var i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE)
                value = unchecked((short)(0xFE00 | il[i++]));

            var opCode = OpCodesByValue[value];
            var size = opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };

            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineString)
                result.Add((opCode.OperandType, BitConverter.ToInt32(il, i)));

            i += size;
        }

        return result;
    }
}
