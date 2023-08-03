using NUnit.Framework;

namespace NexNet.Generator.Tests;

class GeneratorTests
{
    [Test]
    public void MustBePartial_Server()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustBePartial.Id));
    }

    [Test]
    public void MustBePartial_Client()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustBePartial.Id));
    }

    [Test]
    public void MustNotBeAbstract_Server()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustNotBeAbstractOrInterface.Id));

    }

    [Test]
    public void MustNotBeAbstract_Client()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustNotBeAbstractOrInterface.Id));

    }

    [Test]
    public void NexusMustNotBeGeneric()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.NexusMustNotBeGeneric.Id));

    }

    [Test]
    public void InvokeMethodCoreReservedMethodName()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvokeMethodCoreReservedMethodName.Id));

    }

    [Test]
    public void NexusMustNotBeGeneric_Client()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.NexusMustNotBeGeneric.Id));

    }

    [Test]
    public void NexusMustNotBeGeneric_Server()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.NexusMustNotBeGeneric.Id));

    }




    [Test]
    public void CanGenerateClientOnly()
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
        Assert.IsEmpty(diagnostic);

    }

    [Test]
    public void DuplicatedMethodId_Client()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id));
    }


    [Test]
    public void DuplicatedMethodId_Server()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id));
    }

    [Test]
    public void InvalidReturnValue_Client()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidReturnValue.Id));
    }

    [Test]
    public void InvalidReturnValue_Server()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidReturnValue.Id));
    }

    [Test]
    public void CompilesSimpleServerAndClient()
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
        Assert.IsEmpty(diagnostic);
    }

    [Test]
    public void CompilesSimpleServerAndClientWithNullableReturn()
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
        Assert.IsEmpty(diagnostic);
    }

    [Test]
    public void InvalidCancellationToken_Client()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidCancellationToken.Id));
    }

    [Test]
    public void InvalidCancellationToken_Server()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidCancellationToken.Id));
    }


    [Test]
    public void CancellationTokenOnVoid_Client()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id));
    }

    [Test]
    public void CancellationTokenOnVoid_Server()
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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id));
    }

    [Test]
    public void CompilesWhenInterfacesAndNexusesAreInSeparateNamespaces()
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
        Assert.IsEmpty(diagnostic);
    }

}

