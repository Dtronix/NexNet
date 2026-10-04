using NUnit.Framework;

namespace NexNet.Generator.Tests;

class GeneratorChannelTests
{
    [Test]
    public void GeneratesChannelOfNexusObjectStruct()
    {
        // INexusDuplexUnmanagedChannel<T> was removed; struct channels use INexusDuplexChannel<T> with [NexusObject].
        var diagnostic = CSharpGeneratorRunner.RunGenerator(@"
using NexNet;
using NexNet.Pipes;
using NexNet.Serialization;
using System.Threading.Tasks;
namespace NexNetDemo;
[NexusObject]
public struct Sample { [NexusKey(0)] public int Id { get; set; } [NexusKey(1)] public long Count { get; set; } }
partial interface IClientNexus { }
partial interface IServerNexus {  ValueTask Update(INexusDuplexChannel<Sample> pipe); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  public ValueTask Update(INexusDuplexChannel<Sample> pipe){ return default; }
  }
");
        Assert.That(diagnostic, Is.Empty);
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
        Assert.That(diagnostic, Is.Empty);
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

