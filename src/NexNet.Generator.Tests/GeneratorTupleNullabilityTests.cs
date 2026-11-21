using Microsoft.CodeAnalysis;

namespace NexNet.Generator.Tests;

/// <summary>
/// Nullable reference annotations inside tuple elements (and other nested type arguments) must survive into the
/// generated proxies and nexus code, or the generated members mismatch the interface (CS8613/CS8614/CS8619/CS8620).
/// </summary>
class GeneratorTupleNullabilityTests
{
    private static Diagnostic[] Run(string serverMethods, string implementation, string clientMethods = "", string clientImplementation = "")
    {
        return CSharpGeneratorRunner.RunGenerator($$"""
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NexNet;
using NexNet.Collections;
using NexNet.Pipes;
namespace NexNetDemo;
partial interface IClientNexus { {{clientMethods}} }
partial interface IServerNexus { {{serverMethods}} }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus { {{clientImplementation}} }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { {{implementation}} }
""", minDiagnostic: DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task NullableTupleElementInReturnBuildsWarningFree()
    {
        var diagnostics = Run(
            "ValueTask<(int, string?, double)> Get();",
            "public ValueTask<(int, string?, double)> Get() => new((1, null, 2.0));");
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task NullableTupleElementInParameterBuildsWarningFree()
    {
        var diagnostics = Run(
            "ValueTask Set((int, string?, double) value);",
            "public ValueTask Set((int, string?, double) value) => default;");
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task NestedNullableTupleElementsBuildWarningFree()
    {
        var diagnostics = Run(
            "ValueTask<(string?, (int, List<string?>?)?, Tuple<string?, int>?)> Echo((string?, (int, List<string?>?)?, Tuple<string?, int>?) value);",
            "public ValueTask<(string?, (int, List<string?>?)?, Tuple<string?, int>?)> Echo((string?, (int, List<string?>?)?, Tuple<string?, int>?) value) => new(value);");
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task NullableTupleElementsOnClientNexusBuildWarningFree()
    {
        var diagnostics = Run("", "",
            "ValueTask<(int, string?)> Echo((string?, int) value, List<(string?, string)> list);",
            "public ValueTask<(int, string?)> Echo((string?, int) value, List<(string?, string)> list) => new((value.Item2, value.Item1));");
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task SevenElementTupleWithNullableElementsBuildsWarningFree()
    {
        var diagnostics = Run(
            "ValueTask<(int, string?, int, int, int, int, string?)> Get((int, string?, int, int, int, int, string?) value);",
            "public ValueTask<(int, string?, int, int, int, int, string?)> Get((int, string?, int, int, int, int, string?) value) => new(value);");
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
    }
}
