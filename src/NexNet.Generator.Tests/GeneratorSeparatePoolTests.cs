using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using TUnit.Assertions.Enums;

namespace NexNet.Generator.Tests;

/// <summary>
/// [NexusMethod(SeparatePool = true)]: accepted on plain methods, warned about (and without effect) on pipe and channel
/// methods, and never part of the method hash.
/// </summary>
class GeneratorSeparatePoolTests
{
    // The id of the warning for SeparatePool on a method with a pipe or channel parameter.
    private const string SeparatePoolOnPipeMethodId = "NEXNET039";

    [Test]
    public async Task SeparatePool_OnPlainMethod_CompilesWithoutDiagnostics()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus
{
    [NexusMethod(SeparatePool = true)]
    ValueTask Fetch(string remote);
    [NexusMethod(5, SeparatePool = true)]
    ValueTask<int> Merge(int id, CancellationToken cancellationToken);
    [NexusMethod(SeparatePool = false)]
    ValueTask Plain();
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus { }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public ValueTask Fetch(string remote) => ValueTask.CompletedTask;
    public ValueTask<int> Merge(int id, CancellationToken cancellationToken) => ValueTask.FromResult(id);
    public ValueTask Plain() => ValueTask.CompletedTask;
}
""", minDiagnostic: DiagnosticSeverity.Warning);

        await Assert.That(diagnostics).IsEmpty().Because(string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));
    }

    [Test]
    public async Task SeparatePool_OnPipeMethod_ReportsWarning()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus
{
    [NexusMethod(SeparatePool = true)]
    ValueTask Update(INexusDuplexPipe pipe);
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus { }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public ValueTask Update(INexusDuplexPipe pipe) => ValueTask.CompletedTask;
}
""", minDiagnostic: DiagnosticSeverity.Warning);

        var warning = diagnostics.Where(d => d.Id == SeparatePoolOnPipeMethodId).ToArray();
        await Assert.That(warning.Length).IsEqualTo(1).Because(string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));
        await Assert.That(warning[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(warning[0].GetMessage()).Contains("Update");
        await Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task SeparatePool_OnChannelMethod_ReportsWarning()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus
{
    [NexusMethod(SeparatePool = true)]
    ValueTask Stream(INexusDuplexChannel<int> channel);
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus { }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public ValueTask Stream(INexusDuplexChannel<int> channel) => ValueTask.CompletedTask;
}
""", minDiagnostic: DiagnosticSeverity.Warning);

        var warning = diagnostics.Where(d => d.Id == SeparatePoolOnPipeMethodId).ToArray();
        await Assert.That(warning.Length).IsEqualTo(1).Because(string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));
        await Assert.That(warning[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(warning[0].GetMessage()).Contains("Stream");
        await Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task SeparatePool_DoesNotChangeMethodHash()
    {
        const string template = """
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus
{
    ATTRIBUTE
    ValueTask<int> Fetch(string remote);
    ValueTask Plain();
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus { }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public ValueTask<int> Fetch(string remote) => ValueTask.FromResult(0);
    public ValueTask Plain() => ValueTask.CompletedTask;
}
""";

        var plain = await ServerNexusHash(template.Replace("ATTRIBUTE", ""));
        var separate = await ServerNexusHash(template.Replace("ATTRIBUTE", "[NexusMethod(SeparatePool = true)]"));
        var explicitFalse = await ServerNexusHash(template.Replace("ATTRIBUTE", "[NexusMethod(SeparatePool = false)]"));

        await Assert.That(separate).IsEquivalentTo(plain, CollectionOrdering.Matching);
        await Assert.That(explicitFalse).IsEquivalentTo(plain, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The MethodHash values the generator emits in ServerNexus's file (the nexus's and its proxy's), sorted; asserts
    /// the source compiles.
    /// </summary>
    private static async Task<int[]> ServerNexusHash(string source)
    {
        var compilation = CSharpGeneratorRunner.CreateCompilation(source);
        var (_, result, errors) = CSharpGeneratorRunner.RunGeneratorWithResult(compilation);
        await Assert.That(errors).IsEmpty().Because(string.Join(Environment.NewLine, errors.Select(d => d.ToString())));

        var hashes = result.GeneratedTrees
            .Select(t => t.ToString())
            .Where(text => text.Contains("partial class ServerNexus"))
            .SelectMany(text => Regex.Matches(text, @"IInvocationMethodHash\.MethodHash\s*\{\s*get\s*=>\s*(-?\d+)\s*;")
                .Select(m => int.Parse(m.Groups[1].Value)))
            .Distinct()
            .OrderBy(h => h)
            .ToArray();

        await Assert.That(hashes).IsNotEmpty().Because("No MethodHash found in the generated ServerNexus.");
        return hashes;
    }
}
