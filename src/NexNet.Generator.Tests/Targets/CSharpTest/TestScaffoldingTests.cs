using NexNet.Generator.Targets.CSharpTest;

namespace NexNet.Generator.Tests.Targets.CSharpTest;

public class TestScaffoldingTests
{
    private static ScaffoldRun? _basic;
    private static ScaffoldRun? _runtime;

    private static ScaffoldRun Basic => _basic ??= ScaffoldTestRunner.Run(ScaffoldFixtures.BasicInterfaces, ScaffoldFixtures.BasicAttribute);

    private static ScaffoldRun RuntimeRun => _runtime ??= ScaffoldTestRunner.Run(ScaffoldFixtures.Runtime, ScaffoldFixtures.RuntimeProbe);

    private static async Task AssertClean(ScaffoldRun run)
    {
        await Assert.That(run.Errors.Select(d => d.ToString())).IsEmpty();
        await Assert.That(run.GeneratedWarnings.Select(d => d.ToString())).IsEmpty();
        await Assert.That(run.GeneratorDiagnostics.Where(d => d.Id.StartsWith("NEXNET", StringComparison.Ordinal)).Select(d => d.ToString())).IsEmpty();
    }

    // ------------------------------------------------------------------ snapshots

    [Test]
    public Task BasicRemoteProxySnapshot()
        => ScaffoldSnapshots.Verify("basic.Remote_IServerNexus", Basic.File("NexNet.IntegrationTests.TestInterfaces.Remote_IServerNexus"));

    [Test]
    public Task BasicScriptFacadeSnapshot()
        => ScaffoldSnapshots.Verify("basic.ClientNexusScript", Basic.File("NexNet.IntegrationTests.TestInterfaces.ClientNexusScript"));

    [Test]
    public Task RuntimeRemoteProxySnapshot() => ScaffoldSnapshots.Verify("runtime.Remote_IRtServer", RuntimeRun.File("Rt.Remote_IRtServer"));

    [Test]
    public Task RuntimeScriptFacadeSnapshot() => ScaffoldSnapshots.Verify("runtime.RtClientScript", RuntimeRun.File("Rt.RtClientScript"));

    // ------------------------------------------------------------------ compilation

    [Test]
    public async Task BasicCompilesWithoutWarnings()
    {
        await AssertClean(Basic);
        await Assert.That(Basic.Scaffolding.Keys.OrderBy(k => k, StringComparer.Ordinal)).IsEquivalentTo(new[]
        {
            TestScaffoldingExtractor.HintPrefix + "NexNet.IntegrationTests.TestInterfaces.ClientNexusScript.g.cs",
            TestScaffoldingExtractor.HintPrefix + "NexNet.IntegrationTests.TestInterfaces.Remote_IServerNexus.g.cs",
        });
    }

    [Test]
    public async Task RuntimeFixtureCompilesWithoutWarnings() => await AssertClean(RuntimeRun);

    [Test]
    public async Task VersionedInterfacesCompileWithoutWarnings()
    {
        var run = ScaffoldTestRunner.Run(ScaffoldFixtures.VersionedInterfaces, ScaffoldFixtures.VersionedAttribute);
        await AssertClean(run);

        // Inherited methods are forwarded; the shared client interface is emitted once.
        var remote = run.File("NexNet.IntegrationTests.TestInterfaces.Remote_IVersionedServerNexusV2");
        await Assert.That(remote).Contains("public async global::System.Threading.Tasks.ValueTask<bool> VerifyVersionV1(string version)");
        await Assert.That(remote).Contains("public void RunTaskV1_1()");
        await Assert.That(run.Scaffolding.Keys.Count(k => k.EndsWith("SimpleClientNexusScript.g.cs", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(run.Scaffolding.Count).IsEqualTo(3);
    }

    [Test]
    public async Task NothingIsGeneratedWithoutTheAttribute()
    {
        var run = ScaffoldTestRunner.Run(ScaffoldFixtures.BasicInterfaces);
        await Assert.That(run.Scaffolding).IsEmpty();
        await Assert.That(run.Errors).IsEmpty();
    }

    [Test]
    public async Task OutputIsDeterministic()
    {
        var first = ScaffoldTestRunner.Run(ScaffoldFixtures.Runtime, ScaffoldFixtures.RuntimeProbe);
        var second = ScaffoldTestRunner.Run(ScaffoldFixtures.RuntimeProbe, ScaffoldFixtures.Runtime);
        foreach (var (hint, code) in first.Scaffolding)
        {
            await Assert.That(second.Scaffolding[hint]).IsEqualTo(code);
            await Assert.That(code).DoesNotContain("\r");
        }
    }

    [Test]
    public async Task InterfacesNamedByManyPairsAreEmittedOnce()
    {
        var run = ScaffoldTestRunner.Run(ScaffoldFixtures.BasicInterfaces, ScaffoldFixtures.BasicAttribute, ScaffoldFixtures.BasicAttribute.Replace("IServerNexus, ", "IServerNexus , "));
        await AssertClean(run);
        await Assert.That(run.Scaffolding.Count).IsEqualTo(2);
    }

    [Test]
    public async Task InternalInterfacesGetInternalClasses()
    {
        var run = ScaffoldTestRunner.Run(ScaffoldTestRunner.Usings + """
            [assembly: NexusTestScaffolding<Hidden.IServer, Hidden.IClient>]
            namespace Hidden;
            internal interface IServer
            {
                ValueTask<int> Get(int value);
                [NexusMethod(Ignore = true)] void Skipped(int value);
            }
            internal interface IClient { void Tell(string text); }
            """);
        await AssertClean(run);
        var remote = run.File("Hidden.Remote_IServer");
        await Assert.That(remote).Contains("internal sealed partial class Remote_IServer");
        await Assert.That(remote).Contains("void global::Hidden.IServer.Skipped(int value) => throw new global::System.NotSupportedException(");
        await Assert.That(run.File("Hidden.ClientScript")).Contains("internal sealed partial class ClientScript");
    }

    // ------------------------------------------------------------------ NEXNET049

    private static async Task<string[]> Invalid(string source)
    {
        var run = ScaffoldTestRunner.Run(ScaffoldTestRunner.Usings + source);
        var messages = run.Messages("NEXNET049");
        await Assert.That(messages).IsNotEmpty();
        await Assert.That(run.Errors.Where(d => d.Id != "NEXNET049").Select(d => d.ToString())).IsEmpty();
        return messages;
    }

    [Test]
    public async Task NonInterfaceTypeArgumentIsReported()
    {
        var messages = await Invalid("""
            [assembly: NexusTestScaffolding<Bad.Server, Bad.IClient>]
            namespace Bad;
            public class Server { }
            public interface IClient { void Tell(); }
            """);
        await Assert.That(messages.Single()).Contains("'Bad.Server' is not an interface");
    }

    [Test]
    public async Task ProxyMemberCollisionIsReported()
    {
        var messages = await Invalid("""
            [assembly: NexusTestScaffolding<Bad.IServer, Bad.IClient>]
            namespace Bad;
            public interface IServer { ValueTask Invoker(); }
            public interface IClient { void Tell(); }
            """);
        await Assert.That(messages.Single()).Contains("the member 'Invoker' collides with a member of the generated remote proxy 'Remote_IServer'");
    }

    [Test]
    public async Task ScriptMemberCollisionIsReported()
    {
        var messages = await Invalid("""
            [assembly: NexusTestScaffolding<Bad.IServer, Bad.IClient>]
            namespace Bad;
            public interface IServer { void Run(); }
            public interface IClient { void Tell(int value); void TellArgs(); }
            """);
        await Assert.That(messages.Single()).Contains("the generated member 'TellArgs' of 'ClientScript' is declared for both 'Tell' and 'TellArgs'");
    }

    [Test]
    public async Task RecordMemberCollisionIsReported()
    {
        var messages = await Invalid("""
            [assembly: NexusTestScaffolding<Bad.IServer, Bad.IClient>]
            namespace Bad;
            public interface IServer { void Run(); }
            public interface IClient { void Tell(int Deconstruct); }
            """);
        await Assert.That(messages.Single()).Contains("the parameter 'Deconstruct' of 'Tell' collides with a member of the generated record 'TellArgs'");
    }

    [Test]
    public async Task DuplicateMethodNamesAreReported()
    {
        var messages = await Invalid("""
            [assembly: NexusTestScaffolding<Bad.IServer, Bad.IClient>]
            namespace Bad;
            public interface IServer { void Run(); void Run(int value); }
            public interface IClient { void Tell(); }
            """);
        await Assert.That(messages.Single()).Contains("more than one method named 'Run'");
    }

    [Test]
    public async Task GeneratedNameCollisionIsReported()
    {
        var messages = await Invalid("""
            [assembly: NexusTestScaffolding<Bad.IServer, Bad.IFoo>]
            [assembly: NexusTestScaffolding<Bad.IServer, Bad.Foo>]
            namespace Bad;
            public interface IServer { void Run(); }
            public interface IFoo { void Tell(); }
            public interface Foo { void Tell(); }
            """);
        await Assert.That(messages.Single()).Contains("the generated script facade 'Bad.FooScript' has the same name");
    }

    // ------------------------------------------------------------------ runtime

    [Test]
    public async Task GeneratedCodeRunsAgainstAFakeInvoker()
    {
        await AssertClean(RuntimeRun);
        var assembly = RuntimeRun.Load();
        var run = assembly.GetType("Rt.Probe")!.GetMethod("Run")!;
        var transcript = await (Task<string>)run.Invoke(null, null)!;
        await ScaffoldSnapshots.Verify("runtime.transcript", transcript + "\n");
    }
}
