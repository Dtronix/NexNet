
namespace NexNet.Generator.Tests;

class GeneratorCollectionTests
{
    [Test]
    public async Task GeneratesCollection_WithInvocationId()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional, 1)]
INexusList<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  }
");
        await Assert.That(diagnostic).IsEmpty();
    }
    
    [Test]
    public async Task GeneratesCollection_WithoutInvocationId()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional)]
INexusList<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  }
");
        await Assert.That(diagnostic).IsEmpty();
    }
    
    [Test]
    public async Task GeneratesMultipleCollections()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional)]
INexusList<int> NumberList { get; }
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional)]
INexusList<int> NumberList2 { get; }
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional)]
INexusList<int> NumberList3 { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  }
");
        await Assert.That(diagnostic).IsEmpty();
    }

    [Test]
    public async Task Diagnostic_015()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional)]
List<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
}
");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CollectionUnknownType.Id)).IsTrue();
    }
    
    [Test]
    public async Task Diagnostic_016()
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
[NexusCollectionAttribute((NexusCollectionMode)214)]
INexusList<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
}
");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CollectionUnknownMode.Id)).IsTrue();
    }  
    
    [Test]
    public async Task Diagnostic_007_DuplicatedWithCollection()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional, 1)]
INexusList<int> NumberList { get; }
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional, 1)]
INexusList<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
}
");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id)).IsTrue();
    }
    
    [Test]
    public async Task Diagnostic_007_DuplicatedWithMethod()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional, 1)]
INexusList<int> NumberList { get; }
[NexusMethod(1)] void Update1();
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
}
");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id)).IsTrue();
    }
    
    [Test]
    public async Task Diagnostic_017()
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
INexusList<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
}
");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CollectionAttributeMissing.Id)).IsTrue();
    }
    
    [Test]
    public async Task Diagnostic_018()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator(@"
using NexNet;
using NexNet.Pipes;
using System.Threading.Tasks;
using NexNet.Collections.Lists;
using NexNet.Collections;
namespace NexNetDemo;
partial interface IClientNexus {
[NexusCollectionAttribute(NexusCollectionMode.BiDirectional, 1)]
INexusList<int> NumberList { get; }
}
partial interface IServerNexus { }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
}
");
        await Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CollectionCanNotBeOnClient.Id)).IsTrue();
    }
}
