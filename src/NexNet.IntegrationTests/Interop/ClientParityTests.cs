using System.Reflection;
using System.Reflection.Emit;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.InProc;
using NexNet.Interop.Remote;

namespace NexNet.IntegrationTests.Interop;

/// <summary>
/// The parity audit of the client matrix: every test on the matrix (<see cref="ClientMatrixAttribute"/>,
/// <see cref="ClientMatrixAttribute{T}"/>, <see cref="CrossLanguageSmokeAttribute"/>) has a runnable row for every
/// client language and checks the same thing on each, unless it is on the documented exception list
/// (<see cref="ClientParityExceptions"/>). A parity gap therefore cannot appear, or stay after it is fixed, silently.
/// </summary>
/// <remarks>
/// <para>
/// The audit reads the attributes, not <c>NEXNET_TEST_LANGS</c> or what the hosts report: it builds each language's rows
/// with <see cref="ClientMatrixAttribute.Plan"/> from the built-in table (<see cref="ClientLanguages.GetBuiltIn"/>) and
/// the default transports, as PR CI does. The matrix generates only applicable rows, so a language has no runnable row
/// when its transports or capabilities rule out every row, or when the attribute's <c>Languages</c> leaves it out (a
/// test of C#-only API by design). No runtime skip is involved: in CI every skipped row must cite a card
/// (<see cref="MatrixCoverage"/>).
/// </para>
/// <para>
/// A test that runs on a language can still check part of its behavior on the C# client only: a branch on
/// <see cref="ClientLanguages.CSharp"/>, a check or cast to an in-process driver type (<c>client as InProcClientDriver&lt;,&gt;</c>,
/// <c>client is InProcClientDriver&lt;,&gt;</c>), or <c>MatrixTestBase.InProc</c>. The audit finds those in the IL of the test
/// (its async state machine, lambdas and local functions included, and the methods of this assembly they call), and
/// they need an exception of their own (<see cref="ParityGap.CSharpOnlyAssertion"/>). A helper whose driver-type check
/// adds a C# check on top of one that runs on every language (<c>MatrixTestBase.AssertNotOutstanding</c>) is exempt
/// instead, with its reason, in <see cref="ClientParityExceptions.DriverTypeChecks"/>.
/// </para>
/// <para>
/// The same goes for a branch on any other language: a string comparison against a <see cref="ClientLanguages"/> name
/// (<c>target.Language == ClientLanguages.Java</c>) or the start of a family of names
/// (<c>target.Language.StartsWith("browser-")</c>), or a check of a remote driver type (<c>client is RemoteClientDriver</c>),
/// in the test or in a helper it calls (<c>IsBrowser(target)</c>). Such a
/// test checks something else, often less, on that language, and needs an exception with the platform reason
/// (<see cref="ParityGap.PlatformVariantAssertion"/>).
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
            .Select(entry => entry.ToString())
            .Concat(ClientParityExceptions.DriverTypeChecks
                .Where(entry => Audit(ClientParityExceptions.All, ClientParityExceptions.DriverTypeChecks.Where(e => !ReferenceEquals(e, entry)).ToArray()).Count == 0)
                .Select(entry => entry.ToString()));
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
        var helpers = ClientParityExceptions.DriverTypeChecks;
        await Assert.That(helpers.Where(e => string.IsNullOrWhiteSpace(e.Reason))).IsEmpty();
        await Assert.That(helpers.GroupBy(e => e.Helper).Where(g => g.Count() > 1).Select(g => g.Key)).IsEmpty();
    }

    [Test]
    public async Task Audit_FindsAMissingRow()
    {
        // A test the exceptions do not cover is reported, with its class, method and languages.
        var issues = Audit(ClientParityExceptions.All.Where(e => e.Test != ClientParityExceptions.Probe).ToArray());
        await Assert.That(issues).Count().IsEqualTo(1);
        await Assert.That(issues[0]).Contains(ClientParityExceptions.Probe).And.Contains("browser");
    }

    [Test]
    public async Task Audit_FindsABranchOnALanguage()
    {
        // A branch on the browser family (an IsBrowser helper) that the exceptions do not cover is reported, as a variant.
        const string test = "ClientTransportConformanceTests.WebSocket_TextMessage_ClientClosesWith1003";
        var issues = Audit(ClientParityExceptions.All.Where(e => e.Test != test).ToArray());
        await Assert.That(issues).Count().IsEqualTo(1);
        await Assert.That(issues[0]).Contains(test).And.Contains("branches on the language").And.Contains("browser");
    }

    [Test]
    public async Task Audit_FindsADriverTypeCheck()
    {
        // A helper that branches on the driver type (client as InProcClientDriver<,>) checks the C# client only. Without
        // its exemption, every matrix test that calls it is reported, as a C#-only assertion.
        var issues = Audit(ClientParityExceptions.All, []);
        await Assert.That(issues).IsNotEmpty();
        await Assert.That(issues.All(i => i.Contains("an assertion runs on the C# client only", StringComparison.Ordinal))).IsTrue();
        await Assert.That(issues).Contains(i => i.StartsWith("NexusClientTests_Cancellation.CanceledInvocationReleasesItsIdOnTheCanceledReply:", StringComparison.Ordinal));
        await Assert.That(issues).Contains(i => i.StartsWith("InvocationReservedIdTests.ReservedIds_LeaveOutACallWaitingForCredit_AndKeepACallCanceledAfterSend:", StringComparison.Ordinal));
    }

    [Test]
    public async Task Audit_KnowsTheDriverTypes()
    {
        await Assert.That(DriverLanguages(typeof(InProcClientDriver<ClientNexus, ClientNexus.ServerProxy>), out var csharp)).IsEqualTo(ParityLanguages.None);
        await Assert.That(csharp).IsTrue();
        await Assert.That(DriverLanguages(typeof(InProcPoolDriver<,>), out csharp)).IsEqualTo(ParityLanguages.None);
        await Assert.That(csharp).IsTrue();
        await Assert.That(DriverLanguages(typeof(RemoteClientDriver), out csharp)).IsEqualTo(ParityLanguages.All);
        await Assert.That(csharp).IsFalse();
        await Assert.That(DriverLanguages(typeof(RemotePoolDriver), out _)).IsEqualTo(ParityLanguages.All);
        await Assert.That(DriverLanguages(typeof(IClientUnderTest), out _)).IsNull();
        await Assert.That(DriverLanguages(typeof(string), out _)).IsNull();
    }

    [Test]
    public async Task Audit_KnowsEveryLanguageName()
    {
        await Assert.That(UngroupedLanguages()).IsEmpty();
        await Assert.That(NamedLanguages(ClientLanguages.Java, out _)).IsEqualTo(ParityLanguages.Java);
        await Assert.That(NamedLanguages(ClientLanguages.Node, out _)).IsEqualTo(ParityLanguages.Node);
        await Assert.That(NamedLanguages(ClientLanguages.BrowserWebKit, out _)).IsEqualTo(ParityLanguages.Browser);
        await Assert.That(NamedLanguages("browser-", out _)).IsEqualTo(ParityLanguages.Browser);
        await Assert.That(NamedLanguages("browser", out _)).IsEqualTo(ParityLanguages.Browser);
        await Assert.That(NamedLanguages(ClientLanguages.CSharp, out var csharp)).IsEqualTo(ParityLanguages.None);
        await Assert.That(csharp).IsTrue();
        await Assert.That(NamedLanguages("client", out _)).IsNull();
        await Assert.That(NamedLanguages("", out _)).IsNull();
    }

    /// <summary>The audit: what is missing from <paramref name="exceptions"/>, and what in it is stale.</summary>
    /// <param name="exceptions">The documented gaps.</param>
    /// <param name="driverTypeChecks">The helpers whose driver-type checks are exempt; <see cref="ClientParityExceptions.DriverTypeChecks"/> by default.</param>
    internal static IReadOnlyList<string> Audit(IReadOnlyList<ParityException> exceptions, IReadOnlyList<DriverTypeCheckExemption>? driverTypeChecks = null)
    {
        var issues = new List<string>();
        foreach (var language in UngroupedLanguages())
            issues.Add($"ClientLanguages.{language}: a client language the audit has no group for; add it to ClientParityTests.Languages and ParityLanguages.");

        var exempt = new List<MethodBase>();
        foreach (var entry in driverTypeChecks ?? ClientParityExceptions.DriverTypeChecks)
        {
            if (FindHelper(entry.Helper) is not { } helper)
                issues.Add($"{entry.Helper}: the driver-type check exemption names no method of this assembly. Fix or remove it in ClientParityExceptions.DriverTypeChecks.");
            else if (!ChecksDriverType(helper))
                issues.Add($"{entry.Helper}: the driver-type check exemption is stale: the method checks no driver type. Remove it from ClientParityExceptions.DriverTypeChecks.");
            else
                exempt.Add(helper);
        }

        var gaps = FindGaps(issues, exempt);

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
        ParityGap.CSharpOnlyAssertion => "an assertion runs on the C# client only (a branch on ClientLanguages.CSharp or on the driver type, or MatrixTestBase.InProc)",
        ParityGap.PlatformVariantAssertion => "the test branches on the language and checks something else (or less)",
        _ => gap.ToString(),
    };

    private static string Names(ParityLanguages languages)
        => string.Join(", ", new[] { ParityLanguages.Java, ParityLanguages.Node, ParityLanguages.Browser }
            .Where(l => languages.HasFlag(l))
            .Select(l => l.ToString().ToLowerInvariant()));

    /// <summary>Every gap of every matrix test, by test (<c>Class.Method</c>) and kind.</summary>
    private static Dictionary<(string Test, ParityGap Gap), ParityLanguages> FindGaps(List<string> issues, IReadOnlyList<MethodBase> exempt)
    {
        var gaps = new Dictionary<(string, ParityGap), ParityLanguages>();
        var calls = MatrixHelpers() with { DriverCheckExempt = exempt };

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
                    if (!source.HasRunnableRow(language))
                        noRow |= group;
                }

                if (noRow != ParityLanguages.None)
                    gaps[(test, ParityGap.NoRunnableRow)] = noRow;

                var runs = ParityLanguages.All & ~noRow;
                if (body.HasCSharpOnlyAssertion && runs != ParityLanguages.None)
                    gaps[(test, ParityGap.CSharpOnlyAssertion)] = runs;

                if ((body.Variants & runs) != ParityLanguages.None)
                    gaps[(test, ParityGap.PlatformVariantAssertion)] = body.Variants & runs;
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

        /// <summary>
        /// Whether the matrix generates a row of <paramref name="language"/>, by the built-in table (default transports, as
        /// on PR CI).
        /// </summary>
        public bool HasRunnableRow(string language)
        {
            if (Languages.Length > 0 && !Languages.Contains(language))
                return false;

            var plan = new ClientMatrixAttribute { Transports = Transports, CSharpTransports = CSharpTransports, Requires = Requires, Languages = [language] }
                .Plan(transportOverride: null, ClientLanguages.GetBuiltIn);
            return plan.Rows.Any(r => r.Language == language);
        }
    }

    // ----- The IL of a test -----

    /// <param name="CSharpOnly">The tokens of the helpers that check the C# client only (<c>MatrixTestBase.InProc</c>).</param>
    /// <param name="DriverCheckExempt">The helpers whose driver-type checks are exempt (<see cref="ClientParityExceptions.DriverTypeChecks"/>).</param>
    private sealed record Helpers(int[] CSharpOnly, IReadOnlyList<MethodBase> DriverCheckExempt);

    /// <summary>What the IL of a test (and of the helpers it calls) does.</summary>
    /// <param name="HasCSharpOnlyAssertion">
    /// It branches on <see cref="ClientLanguages.CSharp"/>, checks or casts to an in-process driver type
    /// (<c>client as InProcClientDriver&lt;,&gt;</c>), or calls <c>MatrixTestBase.InProc</c>.
    /// </param>
    /// <param name="Variants">The languages it branches on, other than C#.</param>
    private readonly record struct BodyScan(bool HasCSharpOnlyAssertion, ParityLanguages Variants)
    {
        public BodyScan Or(BodyScan other)
            => new(HasCSharpOnlyAssertion || other.HasCSharpOnlyAssertion, Variants | other.Variants);
    }

    /// <summary>The metadata tokens of the <c>MatrixTestBase</c> helpers the audit looks for.</summary>
    private static Helpers MatrixHelpers()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        int Token(string name) => (typeof(MatrixTestBase).GetMethod(name, flags)
                                   ?? throw new InvalidOperationException($"MatrixTestBase.{name} not found: update the parity audit.")).MetadataToken;

        return new Helpers([Token("InProc")], []);
    }

    /// <summary>The method a <see cref="DriverTypeCheckExemption.Helper"/> names (<c>Class.Method</c>), in this assembly.</summary>
    private static MethodBase? FindHelper(string name)
    {
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var dot = name.LastIndexOf('.');
        if (dot <= 0)
            return null;

        var methods = typeof(ClientParityTests).Assembly.GetTypes()
            .Where(t => t.Name == name[..dot])
            .SelectMany(t => t.GetMethods(all))
            .Where(m => m.Name == name[(dot + 1)..])
            .ToArray();
        return methods.Length == 1 ? methods[0] : null;
    }

    /// <summary>
    /// The languages a check or cast to a client driver type (<c>isinst</c>, <c>castclass</c>, <c>typeof</c>) singles
    /// out: an implementation of <see cref="IClientUnderTest"/> or <see cref="IPoolUnderTest"/>. The in-process drivers
    /// drive the C# client only (<paramref name="csharp"/>), the remote drivers every other language. Null when
    /// <paramref name="type"/> is no driver type.
    /// </summary>
    internal static ParityLanguages? DriverLanguages(Type type, out bool csharp)
    {
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        csharp = definition == typeof(InProcClientDriver<,>) || definition == typeof(InProcPoolDriver<,>);
        if (csharp)
            return ParityLanguages.None;

        if (!type.IsClass || type.IsAbstract || !(typeof(IClientUnderTest).IsAssignableFrom(type) || typeof(IPoolUnderTest).IsAssignableFrom(type)))
            return null;

        return ParityLanguages.All;
    }

    /// <summary>Whether the IL of <paramref name="method"/> (state machine and lambdas included) checks a driver type.</summary>
    private static bool ChecksDriverType(MethodBase method)
        => Bodies(method).Any(body => Operands(body).Any(o => o.Operand is OperandType.InlineType or OperandType.InlineTok
                                                              && DriverType(body, o.Token) is { } type && DriverLanguages(type, out _) != null));

    /// <summary>The type <paramref name="token"/> names in <paramref name="body"/>, if it resolves.</summary>
    private static Type? DriverType(MethodBase body, int token)
    {
        try
        {
            return body.Module.ResolveType(
                token,
                body.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null,
                body.IsGenericMethod ? body.GetGenericArguments() : null);
        }
        catch (ArgumentException)
        {
            // A field or method token (ldtoken), or a reference the module cannot resolve without more context: no type.
            return null;
        }
    }

    /// <summary>
    /// Every language name of <see cref="ClientLanguages"/> (its string constants), so a language added there is
    /// recognized in a branch without a change here.
    /// </summary>
    private static readonly string[] LanguageNames = typeof(ClientLanguages)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string) && f.Name != nameof(ClientLanguages.EnvironmentVariable))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    /// <summary>The <see cref="ClientLanguages"/> names the audit has no <see cref="ParityLanguages"/> group for.</summary>
    internal static IEnumerable<string> UngroupedLanguages()
        => LanguageNames.Where(n => n != ClientLanguages.CSharp && Languages.All(l => l.Language != n));

    /// <summary>
    /// The languages a string literal of a test names, for a branch on the language: a language name
    /// (<see cref="ClientLanguages"/>), or the start of a family of names (<c>"browser-"</c>, <c>"browser"</c>), which names
    /// every language of the family. Null when it names none.
    /// </summary>
    /// <param name="literal">The literal.</param>
    /// <param name="csharp">Whether it names the C# client.</param>
    internal static ParityLanguages? NamedLanguages(string literal, out bool csharp)
    {
        csharp = literal == ClientLanguages.CSharp;
        if (csharp)
            return ParityLanguages.None;

        var family = literal.EndsWith('-') ? literal : literal + "-";
        var named = LanguageNames.Where(n => n == literal || (literal.Length > 0 && n.StartsWith(family, StringComparison.Ordinal))).ToArray();
        if (named.Length == 0)
            return null;

        return Languages.Where(l => named.Contains(l.Language)).Aggregate(ParityLanguages.None, (all, l) => all | l.Group);
    }

    /// <summary>
    /// What the IL of the test, its state machine, lambdas and local functions does, and that of the methods of this
    /// assembly they call (an <c>IsBrowser(target)</c> helper, say).
    /// </summary>
    private static BodyScan ScanBodies(MethodInfo test, Helpers helpers)
        => ScanMethod(test, helpers, new Dictionary<MethodBase, BodyScan>());

    private static BodyScan ScanMethod(MethodBase method, Helpers helpers, Dictionary<MethodBase, BodyScan> scanned)
    {
        if (scanned.TryGetValue(method, out var known))
            return known;

        // Recursion (a helper calling itself or back into the test) ends here.
        scanned[method] = default;
        var result = default(BodyScan);
        var exempt = helpers.DriverCheckExempt.Any(m => m.Module == method.Module && m.MetadataToken == method.MetadataToken);
        foreach (var body in Bodies(method))
        {
            // The operand index and languages of the last string literal that names a language: a string comparison right
            // after it (with at most the ClientTarget.Language getter between) is a branch on the language; the name
            // alone (a client named "csharp", a spec field) is not.
            var namedAt = -3;
            var namedCSharp = false;
            var named = ParityLanguages.None;
            var operands = Operands(body);
            for (var i = 0; i < operands.Count; i++)
            {
                var (operand, token) = operands[i];
                if (operand == OperandType.InlineString)
                {
                    if (NamedLanguages(body.Module.ResolveString(token), out var csharp) is { } languages)
                        (namedAt, namedCSharp, named) = (i, csharp, languages);
                }
                else if (operand is OperandType.InlineType or OperandType.InlineTok)
                {
                    // A driver-type check (client as InProcClientDriver<,>, client is RemoteClientDriver) singles out the
                    // languages that driver drives, unless the helper is exempt (ClientParityExceptions.DriverTypeChecks).
                    if (!exempt && DriverType(body, token) is { } type && DriverLanguages(type, out var csharp) is { } languages)
                        result = csharp ? result with { HasCSharpOnlyAssertion = true } : result with { Variants = result.Variants | languages };
                }
                else if (helpers.CSharpOnly.Contains(token))
                {
                    result = result with { HasCSharpOnlyAssertion = true };
                }
                else if (i - namedAt <= 2 && IsStringComparison(body.Module, token))
                {
                    result = namedCSharp ? result with { HasCSharpOnlyAssertion = true } : result with { Variants = result.Variants | named };
                }
                else if (Helper(body, token) is { } helper)
                {
                    result = result.Or(ScanMethod(helper, helpers, scanned));
                }
            }
        }

        scanned[method] = result;
        return result;
    }

    /// <summary>The method of this assembly that <paramref name="token"/> calls from <paramref name="body"/>, if it is not a test.</summary>
    private static MethodBase? Helper(MethodBase body, int token)
    {
        MethodBase? method;
        try
        {
            method = body.Module.ResolveMethod(
                token,
                body.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null,
                body.IsGenericMethod ? body.GetGenericArguments() : null);
        }
        catch (ArgumentException)
        {
            // A member reference the module cannot resolve without more context: not a helper of this assembly.
            return null;
        }

        if (method == null || method.Module != body.Module || method.GetCustomAttribute<TestAttribute>() != null)
            return null;

        return method.IsGenericMethod && method is MethodInfo generic ? generic.GetGenericMethodDefinition() : method;
    }

    private static bool IsStringComparison(Module module, int token)
    {
        try
        {
            return module.ResolveMethod(token) is { DeclaringType: var type, Name: "op_Equality" or "op_Inequality" or "Equals" or "StartsWith" or "EndsWith" or "Contains" or "CompareOrdinal" or "Compare" } && type == typeof(string);
        }
        catch (ArgumentException)
        {
            // A generic member reference: not a string comparison.
            return false;
        }
    }

    /// <summary>The test method and the compiler-generated methods of its body (named <c>&lt;Test&gt;...</c>).</summary>
    private static IEnumerable<MethodBase> Bodies(MethodBase test)
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

    /// <summary>The method, string and type operands of the IL of <paramref name="method"/>.</summary>
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

            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineString or OperandType.InlineType or OperandType.InlineTok)
                result.Add((opCode.OperandType, BitConverter.ToInt32(il, i)));

            i += size;
        }

        return result;
    }
}
