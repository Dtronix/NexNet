
namespace NexNet.Generator.Tests;

class GeneratorPipeTests
{
    [Test]
    public async Task MethodCanNotHaveMoreThanOneDuplexPipe()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask Update(INexusDuplexPipe pipe1, INexusDuplexPipe pipe2); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.TooManyPipes.Id)).IsTrue();
    }

    [Test]
    public async Task NexusDuplexPipeNotAllowedOnVoid()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  void Update(INexusDuplexPipe pipe); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.PipeOnVoidOrReturnTask.Id)).IsTrue();
    }

    [Test]
    public async Task NexusDuplexPipeNotAllowedOnReturnTask()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask<int> Update(INexusDuplexPipe pipe); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.PipeOnVoidOrReturnTask.Id)).IsTrue();
    }

    [Test]
    public async Task NexusDuplexPipeNotAllowedOnMethodWithCancellationToken()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask<int> Update(INexusDuplexPipe pipe, System.Threading.CancellationToken ct); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.PipeOnVoidOrReturnTask.Id)).IsTrue();
    }

    [Test]
    public async Task NexusDuplexPipeGenerates()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
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
        await Assert.That(diagnostic).IsEmpty();
    }

}
