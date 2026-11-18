using Microsoft.CodeAnalysis;

namespace NexNet.Generator.Tests;

/// <summary>
/// NEXNET042: nexus method names must be unique across an interface and its inherited interfaces. Before it existed,
/// the generator threw on such a nexus instead of reporting a diagnostic.
/// </summary>
class GeneratorDuplicateMethodNameTests
{
    private static async Task<Diagnostic[]> RunWithoutGeneratorException(string source)
    {
        // A file path, as in a real build: diagnostics are reported at file locations, which need one.
        var compilation = CSharpGeneratorRunner.CreateCompilation(source);
        var tree = compilation.SyntaxTrees.Single();
        compilation = compilation.ReplaceSyntaxTree(tree, tree.WithFilePath("Source.cs"));
        var (_, result, errors) = CSharpGeneratorRunner.RunGeneratorWithResult(compilation);

        await Assert.That(result.Results.Select(r => r.Exception).OfType<Exception>().ToArray()).IsEmpty().Because("The generator threw.");
        await Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785")).IsFalse().Because("The generator reported a failure.");
        return errors;
    }

    private static Diagnostic[] Nexnet042(Diagnostic[] diagnostics) =>
        diagnostics.Where(d => d.Id == DiagnosticDescriptors.DuplicateMethodName.Id).ToArray();

    [Test]
    public async Task OverloadWithinOneInterface_ReportsNexnet042()
    {
        var errors = await RunWithoutGeneratorException("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus
{
    void Update(int value);
    void Update(string value);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update(int value) { }
    public void Update(string value) { }
}
""");

        var dup = Nexnet042(errors);
        await Assert.That(dup.Length).IsEqualTo(1);
        await Assert.That(dup[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(dup[0].GetMessage()).Contains("'IServerNexus'").And.Contains("'Update'");
    }

    [Test]
    public async Task SameNameAcrossInheritedInterfaces_ReportsNexnet042()
    {
        var errors = await RunWithoutGeneratorException("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexusBase
{
    void Update(int value);
}
partial interface IServerNexus : IServerNexusBase
{
    ValueTask Update(string value);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update(int value) { }
    public ValueTask Update(string value) => default;
}
""");

        var dup = Nexnet042(errors);
        await Assert.That(dup.Length).IsEqualTo(1);
        await Assert.That(dup[0].GetMessage()).Contains("'IServerNexus'").And.Contains("'Update'");
    }

    [Test]
    public async Task SameNameAcrossVersionedInterfaces_ReportsNexnet042()
    {
        var errors = await RunWithoutGeneratorException("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
[NexusVersion(Version = "V1")]
partial interface IServerNexusV1
{
    [NexusMethod(1)]
    void Update(int value);
}
[NexusVersion(Version = "V2")]
partial interface IServerNexusV2 : IServerNexusV1
{
    [NexusMethod(2)]
    void Update(string value);
}
[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update(int value) { }
    public void Update(string value) { }
}
""");

        var dup = Nexnet042(errors);
        await Assert.That(dup.Length).IsEqualTo(1);
        await Assert.That(dup[0].GetMessage()).Contains("'IServerNexusV2'").And.Contains("'Update'");
    }

    [Test]
    public async Task OverloadOnProxyInterface_ReportsNexnet042()
    {
        var errors = await RunWithoutGeneratorException("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus
{
    void Notify(int value);
    void Notify(string value);
}
partial interface IServerNexus { }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus { }
""");

        var dup = Nexnet042(errors);
        await Assert.That(dup.Length).IsEqualTo(1);
        await Assert.That(dup[0].GetMessage()).Contains("'IClientNexus'").And.Contains("'Notify'");
    }

    [Test]
    public async Task Nexnet042_IsReportedAtTheDuplicateMethod()
    {
        var source = """
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus
{
    void Update(int value);
    void Update(string value);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update(int value) { }
    public void Update(string value) { }
}
""";
        var dup = Nexnet042(await RunWithoutGeneratorException(source)).Single();

        var span = dup.Location.GetLineSpan();
        await Assert.That(dup.Location).IsNotEqualTo(Location.None);
        await Assert.That(span.Path).IsEqualTo("Source.cs");
        await Assert.That(span.StartLinePosition.Line).IsEqualTo(6); // void Update(string value);
        await Assert.That(source.Substring(dup.Location.SourceSpan.Start, dup.Location.SourceSpan.Length)).IsEqualTo("Update");
    }

    [Test]
    public async Task SerializationDiagnostic_IsReportedAtTheMember()
    {
        var source = """
using NexNet;
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
partial class DataObject
{
    [NexusKey(0)] public int Keyed { get; set; }
    public int Missing { get; set; }
}
""";
        var errors = await RunWithoutGeneratorException(source);
        var missingKey = errors.Single(d => d.Id == DiagnosticDescriptors.MissingNexusKey.Id);

        await Assert.That(missingKey.Location).IsNotEqualTo(Location.None);
        await Assert.That(missingKey.Location.GetLineSpan().Path).IsEqualTo("Source.cs");
        await Assert.That(missingKey.Location.GetLineSpan().StartLinePosition.Line).IsEqualTo(7); // public int Missing
        await Assert.That(source.Substring(missingKey.Location.SourceSpan.Start, missingKey.Location.SourceSpan.Length)).IsEqualTo("Missing");
    }

    [Test]
    public async Task OverloadedAuthorizedClassMethods_UseTheImplementingOverload()
    {
        // The class overloads an authorized method name with a helper declared first; the interface names are
        // unique. The authorization of the overload that implements the interface method is used.
        var compilation = CSharpGeneratorRunner.CreateCompilation("""
using NexNet;
using System;
using System.Threading.Tasks;
namespace NexNetDemo;
public enum TestPermission { Read, Write }
partial interface IClientNexus { }
partial interface IServerNexus
{
    ValueTask Update(int value);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    [NexusAuthorize<TestPermission>(TestPermission.Read, CacheDurationSeconds = 11)]
    public ValueTask Update(string value) => default;

    [NexusAuthorize<TestPermission>(TestPermission.Write, CacheDurationSeconds = 77)]
    public ValueTask Update(int value) => default;

    protected override ValueTask<AuthorizeResult> OnAuthorize(
        NexNet.Invocation.ServerSessionContext<ServerNexus.ClientProxy> context,
        int methodId, string methodName, ReadOnlyMemory<int> requiredPermissions)
        => new(AuthorizeResult.Allowed);
}
""");
        var (_, result, errors) = CSharpGeneratorRunner.RunGeneratorWithResult(compilation);

        await Assert.That(result.Results.Select(r => r.Exception).OfType<Exception>().ToArray()).IsEmpty().Because("The generator threw.");
        await Assert.That(errors).IsEmpty();
        var nexusSource = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("ServerNexus.Nexus.g.cs")).ToString();
        await Assert.That(nexusSource).Contains("77).ConfigureAwait(false)");
        await Assert.That(nexusSource).DoesNotContain("11).ConfigureAwait(false)");
    }
}
