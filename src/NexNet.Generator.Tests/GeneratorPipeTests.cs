using NexNet.Generator;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

class GeneratorPipeTests
{
    [Test]
    public void MethodCanNotHaveMoreThanOneDuplexPipe()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask Update(INexusDuplexPipe pipe1, INexusDuplexPipe pipe2); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.TooManyPipes.Id));
    }

    [Test]
    public void NexusDuplexPipeNotAllowedOnVoid()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  void Update(INexusDuplexPipe pipe); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.IsTrue(diagnostic.Any(d => d.Id == DiagnosticDescriptors.PipeOnVoid.Id));
    }

    [Test]
    public void NexusDuplexPipeGenerates()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask Update(INexusDuplexPipe pipe); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
    public ValueTask Update(INexusDuplexPipe pipe) => ValueTask.CompletedTask;
}
""");
        Assert.IsEmpty(diagnostic);
    }

}

