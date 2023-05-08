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
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustNotBeAbstract.Id));

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
partial abstract class ServerHub : IServerHub
{
    public void Update() { }
}
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustNotBeAbstract.Id));

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


}

