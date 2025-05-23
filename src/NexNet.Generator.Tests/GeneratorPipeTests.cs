﻿using NUnit.Framework;

namespace NexNet.Generator.Tests;

class GeneratorPipeTests
{
    [Test]
    public void MethodCanNotHaveMoreThanOneDuplexPipe()
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
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.TooManyPipes.Id), Is.True);
    }

    [Test]
    public void NexusDuplexPipeNotAllowedOnVoid()
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
partial interface IServerNexus {  ValueTask<int> Update(INexusDuplexPipe pipe); }
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
partial interface IServerNexus {  ValueTask<int> Update(INexusDuplexPipe pipe, System.Threading.CancellationToken ct); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.PipeOnVoidOrReturnTask.Id), Is.True);
    }

    [Test]
    public void NexusDuplexPipeGenerates()
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
        Assert.That(diagnostic, Is.Empty);
    }

}

