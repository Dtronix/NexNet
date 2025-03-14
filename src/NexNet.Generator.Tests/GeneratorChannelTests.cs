using NUnit.Framework;

namespace NexNet.Generator.Tests;

class GeneratorChannelTests
{
    [Test]
    public void GeneratesUnmanagedChannel()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator(@"
using NexNet;
using NexNet.Pipes;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask Update(INexusDuplexUnmanagedChannel<int> pipe); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  public ValueTask Update(INexusDuplexUnmanagedChannel <int> pipe){ return default; }
  }
");
        Assert.IsEmpty(diagnostic);
    }

    [Test]
    public void GeneratesChannel()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator(@"
using NexNet;
using NexNet.Pipes;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask Update(INexusDuplexChannel<int> pipe); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  public ValueTask Update(INexusDuplexChannel <int> pipe){ return default; }
  }
");
        Assert.IsEmpty(diagnostic);
    }

    [Test]
    public void MethodCanNotHaveMoreThanOneDuplexChannel()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask Update(INexusDuplexChannel<int> pipe1, INexusDuplexChannel<int> pipe2); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.TooManyPipes.Id), Is.True);
    }

    [Test]
    public void NexusDuplexChannelNotAllowedOnVoid()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  void Update(INexusDuplexChannel<int> channel); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.PipeOnVoidOrReturnTask.Id), Is.True);
    }

    [Test]
    public void NexusDuplexPipeNotAllowedOnReturnTask()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask<int> Update(INexusDuplexChannel<int> channel); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.PipeOnVoidOrReturnTask.Id), Is.True);
    }

    [Test]
    public void NexusDuplexPipeNotAllowedOnMethodWithCancellationToken()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Pipes;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask<int> Update(INexusDuplexChannel<int> channel, System.Threading.CancellationToken ct); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.PipeOnVoidOrReturnTask.Id), Is.True);
    }
}

