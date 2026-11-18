using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

/// <summary>
/// NEXNET042: nexus method names must be unique across an interface and its inherited interfaces. Before it existed,
/// the generator threw on such a nexus instead of reporting a diagnostic.
/// </summary>
class GeneratorDuplicateMethodNameTests
{
    private static Diagnostic[] RunWithoutGeneratorException(string source)
    {
        // A file path, as in a real build: diagnostics are reported at file locations, which need one.
        var compilation = CSharpGeneratorRunner.CreateCompilation(source);
        var tree = compilation.SyntaxTrees.Single();
        compilation = compilation.ReplaceSyntaxTree(tree, tree.WithFilePath("Source.cs"));
        var (_, result, errors) = CSharpGeneratorRunner.RunGeneratorWithResult(compilation);

        Assert.That(result.Results.Select(r => r.Exception), Is.All.Null, "The generator threw.");
        Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785"), Is.False, "The generator reported a failure.");
        return errors;
    }

    private static Diagnostic[] Nexnet042(Diagnostic[] diagnostics) =>
        diagnostics.Where(d => d.Id == DiagnosticDescriptors.DuplicateMethodName.Id).ToArray();

    [Test]
    public void OverloadWithinOneInterface_ReportsNexnet042()
    {
        var errors = RunWithoutGeneratorException("""
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
        Assert.That(dup, Has.Length.EqualTo(1));
        Assert.That(dup[0].Severity, Is.EqualTo(DiagnosticSeverity.Error));
        Assert.That(dup[0].GetMessage(), Does.Contain("'IServerNexus'").And.Contain("'Update'"));
    }

    [Test]
    public void SameNameAcrossInheritedInterfaces_ReportsNexnet042()
    {
        var errors = RunWithoutGeneratorException("""
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
        Assert.That(dup, Has.Length.EqualTo(1));
        Assert.That(dup[0].GetMessage(), Does.Contain("'IServerNexus'").And.Contain("'Update'"));
    }

    [Test]
    public void SameNameAcrossVersionedInterfaces_ReportsNexnet042()
    {
        var errors = RunWithoutGeneratorException("""
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
        Assert.That(dup, Has.Length.EqualTo(1));
        Assert.That(dup[0].GetMessage(), Does.Contain("'IServerNexusV2'").And.Contain("'Update'"));
    }

    [Test]
    public void OverloadOnProxyInterface_ReportsNexnet042()
    {
        var errors = RunWithoutGeneratorException("""
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
        Assert.That(dup, Has.Length.EqualTo(1));
        Assert.That(dup[0].GetMessage(), Does.Contain("'IClientNexus'").And.Contain("'Notify'"));
    }

    [Test]
    public void Nexnet042_IsReportedAtTheDuplicateMethod()
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
        var dup = Nexnet042(RunWithoutGeneratorException(source)).Single();

        var span = dup.Location.GetLineSpan();
        Assert.That(dup.Location, Is.Not.EqualTo(Location.None));
        Assert.That(span.Path, Is.EqualTo("Source.cs"));
        Assert.That(span.StartLinePosition.Line, Is.EqualTo(6)); // void Update(string value);
        Assert.That(source.Substring(dup.Location.SourceSpan.Start, dup.Location.SourceSpan.Length), Is.EqualTo("Update"));
    }

    [Test]
    public void SerializationDiagnostic_IsReportedAtTheMember()
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
        var errors = RunWithoutGeneratorException(source);
        var missingKey = errors.Single(d => d.Id == DiagnosticDescriptors.MissingNexusKey.Id);

        Assert.That(missingKey.Location, Is.Not.EqualTo(Location.None));
        Assert.That(missingKey.Location.GetLineSpan().Path, Is.EqualTo("Source.cs"));
        Assert.That(missingKey.Location.GetLineSpan().StartLinePosition.Line, Is.EqualTo(7)); // public int Missing
        Assert.That(source.Substring(missingKey.Location.SourceSpan.Start, missingKey.Location.SourceSpan.Length), Is.EqualTo("Missing"));
    }

    [Test]
    public void OverloadedAuthorizedClassMethods_UseTheImplementingOverload()
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

        Assert.That(result.Results.Select(r => r.Exception), Is.All.Null, "The generator threw.");
        Assert.That(errors, Is.Empty);
        var nexusSource = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("ServerNexus.Nexus.g.cs")).ToString();
        Assert.That(nexusSource, Does.Contain("77).ConfigureAwait(false)"));
        Assert.That(nexusSource, Does.Not.Contain("11).ConfigureAwait(false)"));
    }
}
