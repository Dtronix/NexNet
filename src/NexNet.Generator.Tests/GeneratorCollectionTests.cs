using NUnit.Framework;

namespace NexNet.Generator.Tests;

class GeneratorCollectionTests
{
    [Test]
    public void GeneratesCollection()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator(@"
using NexNet;
using NexNet.Pipes;
using System.Threading.Tasks;
using NexNet.Collections.Lists;
using NexNet.Collections;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {
[NexusCollectionAttribute(1, NexusCollectionMode.BiDrirectional)]
INexusList<int> NumberList { get; set; } 
}
//[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  }
");
        Assert.That(diagnostic, Is.Empty);
    }

}

