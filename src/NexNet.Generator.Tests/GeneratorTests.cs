using Microsoft.CodeAnalysis;

namespace NexNet.Generator.Tests;

class GeneratorTests
{
    [Test]
    public async Task MustBePartial_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustBePartial.Id)).IsTrue();
    }

    [Test]
    public async Task MustBePartial_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustBePartial.Id)).IsTrue();
    }

    [Test]
    public async Task MustNotBeAbstract_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial abstract class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustNotBeAbstractOrInterface.Id)).IsTrue();

    }

    [Test]
    public async Task MustNotBeAbstract_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
abstract partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustNotBeAbstractOrInterface.Id)).IsTrue();

    }

    [Test]
    public async Task NexusMustNotBeGeneric()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
abstract partial class ClientNexus<T> : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.NexusMustNotBeGeneric.Id)).IsTrue();

    }

    [Test]
    public async Task InvokeMethodCoreReservedMethodName()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus<T> : IClientNexus
{
    public void Update() { }

}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
   protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationRequestMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
{
}
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvokeMethodCoreReservedMethodName.Id)).IsTrue();

    }

    [Test]
    public async Task InvokeMethodCoreUsesPoolingBuilder()
    {
        var (_, result, errors) = CSharpGeneratorRunner.RunGeneratorWithResult(CSharpGeneratorRunner.CreateCompilation("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { ValueTask Update(int value); }
partial interface IServerNexus { ValueTask<int> Get(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public ValueTask Update(int value) => ValueTask.CompletedTask;
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public ValueTask<int> Get() => new(1);
}
"""));
        await Assert.That(errors).IsEmpty().Because(string.Join("\n", errors.Select(e => e.ToString())));

        var nexusSources = result.Results.Single().GeneratedSources
            .Select(s => s.SourceText.ToString())
            .Where(s => s.Contains("override async global::System.Threading.Tasks.ValueTask InvokeMethodCore("))
            .ToArray();
        await Assert.That(nexusSources.Length).IsEqualTo(2);
        foreach (var code in nexusSources)
        {
            await Assert.That(code).Contains("[global::System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(global::System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]");
        }
    }

    [Test]
    public async Task NexusMustNotBeGeneric_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus<T> : IClientNexus
{
    public void Update() { }

}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.NexusMustNotBeGeneric.Id)).IsTrue();

    }

    [Test]
    public async Task NexusMustNotBeGeneric_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }

}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus<T> : IServerNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.NexusMustNotBeGeneric.Id)).IsTrue();

    }




    [Test]
    public async Task CanGenerateClientOnly()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic).IsEmpty();

    }

    [Test]
    public async Task DuplicatedMethodId_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { 
[NexusMethod(1)] void Update0();
[NexusMethod(1)] void Update1();
}
partial interface IServerNexus { 

}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id)).IsTrue();
    }


    [Test]
    public async Task DuplicatedMethodId_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { 

}
partial interface IServerNexus { 
    [NexusMethod(1)] void Update0();
    [NexusMethod(1)] void Update1();
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id)).IsTrue();
    }

    [Test]
    public async Task InvalidReturnValue_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { int Update(); }
partial interface IServerNexus { }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidReturnValue.Id)).IsTrue();
    }

    [Test]
    public async Task InvalidReturnValue_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus { int Update();  }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }

""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidReturnValue.Id)).IsTrue();
    }

    [Test]
    public async Task CompilesSimpleServerAndClient()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(string arg); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update(string arg) { }
}
""");
        await Assert.That(diagnostic).IsEmpty();
    }

    [Test]
    public async Task CompilesSimpleServerAndClientWithNullableReturn()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { ValueTask<string?> Update(string[]? val); }
partial interface IServerNexus { }

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{

}
""");
        await Assert.That(diagnostic).IsEmpty();
    }

    [Test]
    public async Task InvalidCancellationToken_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { ValueTask Update(CancellationToken ct, int val); }
partial interface IServerNexus { }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidCancellationToken.Id)).IsTrue();
    }

    [Test]
    public async Task InvalidCancellationToken_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus { ValueTask Update(CancellationToken ct, int val); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidCancellationToken.Id)).IsTrue();
    }


    [Test]
    public async Task CancellationTokenOnVoid_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(CancellationToken ct); }
partial interface IServerNexus { }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id)).IsTrue();
    }

    [Test]
    public async Task CancellationTokenOnVoid_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  void Update(CancellationToken ct); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id)).IsTrue();
    }

    [Test]
    public async Task CompilesWhenInterfacesAndNexusesAreInSeparateNamespaces()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace InterfaceNameSpace1.One.Two
{
    partial interface IClientNexus { void Update(); }
}

namespace InterfaceNameSpace2.Three.Four
{
    partial interface IServerNexus { void Update(string arg); }
}
namespace HubNameSpaces1.Five.Six
{
    [Nexus<InterfaceNameSpace1.One.Two.IClientNexus, InterfaceNameSpace2.Three.Four.IServerNexus>(NexusType = NexusType.Client)]
    partial class ClientNexus : InterfaceNameSpace1.One.Two.IClientNexus
    {
        public void Update() { }
    }
}
namespace HubNameSpaces2.Seven.Eight
{
    [Nexus<InterfaceNameSpace2.Three.Four.IServerNexus, InterfaceNameSpace1.One.Two.IClientNexus>(NexusType = NexusType.Server)]
    partial class ServerNexus : InterfaceNameSpace2.Three.Four.IServerNexus
    {
        public void Update(string arg) { }
    }
}
""");
        await Assert.That(diagnostic).IsEmpty();
    }
}
