using NexNet.Generator;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Generator;

class GeneratorDiagnosticDescriptors
{
    [Test]
    public void MustBePartial_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub
{
    public void Update() { }
}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
class ServerHub : IServerHub
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
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
class ClientHub : IClientHub
{
    public void Update() { }
}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
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
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial abstract class ClientHub : IClientHub
{
    public void Update() { }
}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
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
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub
{
    public void Update() { }
}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
abstract partial class ServerHub : IServerHub
{
    public void Update() { }
}
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustNotBeAbstractOrInterface.Id));

    }

    [Test]
    public void HubMustNotBeGeneric()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
abstract partial class ClientHub<T> : IClientHub
{
    public void Update() { }
}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
{
    public void Update() { }
}
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.HubMustNotBeGeneric.Id));

    }

    [Test]
    public void InvokeMethodCoreReservedMethodName()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub<T> : IClientHub
{
    public void Update() { }

}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
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
    public void HubMustNotBeGeneric_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub<T> : IClientHub
{
    public void Update() { }

}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
{
    public void Update() { }
}
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.HubMustNotBeGeneric.Id));

    }

    [Test]
    public void HubMustNotBeGeneric_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub
{
    public void Update() { }

}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub<T> : IServerHub
{
    public void Update() { }
}
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.HubMustNotBeGeneric.Id));

    }




    [Test]
    public void CanGenerateClientOnly()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub
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
partial interface IClientHub { 
[NexNetMethod(1)] void Update0();
[NexNetMethod(1)] void Update1();
}
partial interface IServerHub { 

}
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub
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
partial interface IClientHub { 

}
partial interface IServerHub { 
    [NexNetMethod(1)] void Update0();
    [NexNetMethod(1)] void Update1();
}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
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
partial interface IClientHub { int Update(); }
partial interface IServerHub { }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub{ }
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub { }
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidReturnValue.Id));
    }    
    
    [Test]
    public void InvalidReturnValue_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { }
partial interface IServerHub { int Update();  }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub{ }
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub { }

""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidReturnValue.Id));
    }

    [Test]
    public void CompilesSimpleServerAndClient()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { void Update(); }
partial interface IServerHub { void Update(string arg); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub
{
    public void Update() { }
}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
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
partial interface IClientHub { ValueTask<string?> Update(string[]? val); }
partial interface IServerHub { }

[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
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
partial interface IClientHub { ValueTask Update(CancellationToken ct, int val); }
partial interface IServerHub { }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub{ }
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub { }
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidCancellationToken.Id));
    }

    [Test]
    public void InvalidCancellationToken_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { }
partial interface IServerHub { ValueTask Update(CancellationToken ct, int val); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub{ }
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub { }
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidCancellationToken.Id));
    }


    [Test]
    public void CancellationTokenOnVoid_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { void Update(CancellationToken ct); }
partial interface IServerHub { }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub{ }
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub { }
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id));
    }

    [Test]
    public void CancellationTokenOnVoid_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { }
partial interface IServerHub {  void Update(CancellationToken ct); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub{ }
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub { }
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id));
    }

    [Test]
    public void CompilesWhenInterfacesAndHubsAreInSeparateNamespaces()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace InterfaceNameSpace1.One.Two
{
    partial interface IClientHub { void Update(); }
}

namespace InterfaceNameSpace2.Three.Four
{
    partial interface IServerHub { void Update(string arg); }
}
namespace HubNameSpaces1.Five.Six
{
    [NexNetHub<InterfaceNameSpace1.One.Two.IClientHub, InterfaceNameSpace2.Three.Four.IServerHub>(NexNetHubType.Client)]
    partial class ClientHub : InterfaceNameSpace1.One.Two.IClientHub
    {
        public void Update() { }
    }
}
namespace HubNameSpaces2.Seven.Eight
{
    [NexNetHub<InterfaceNameSpace2.Three.Four.IServerHub, InterfaceNameSpace1.One.Two.IClientHub>(NexNetHubType.Server)]
    partial class ServerHub : InterfaceNameSpace2.Three.Four.IServerHub
    {
        public void Update(string arg) { }
    }
}
""");
        Assert.IsEmpty(diagnostic);
    }

    [Test]
    public void CompilesWithNullableArguments()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientHub { void Update(long? arg); }
partial interface IServerHub { void Update(long? arg); }
[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub
{
    public void Update(long? arg) { }
}
[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
{
    public void Update(long? arg) { }
}
""");
        Assert.IsEmpty(diagnostic);
    }

}

