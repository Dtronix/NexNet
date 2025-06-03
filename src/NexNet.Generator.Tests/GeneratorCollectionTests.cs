using NUnit.Framework;

namespace NexNet.Generator.Tests;

class GeneratorCollectionTests
{
    [Test]
    public void GeneratesCollection_WithInvocationId()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDrirectional, 1)]
INexusList<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  }
");
        Assert.That(diagnostic, Is.Empty);
    }
    
    [Test]
    public void GeneratesCollection_WithoutInvocationId()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDrirectional)]
INexusList<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  }
");
        Assert.That(diagnostic, Is.Empty);
    }
    
    [Test]
    public void GeneratesMultipleCollections()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDrirectional)]
INexusList<int> NumberList { get; }
[NexusCollectionAttribute(NexusCollectionMode.BiDrirectional)]
INexusList<int> NumberList2 { get; }
[NexusCollectionAttribute(NexusCollectionMode.BiDrirectional)]
INexusList<int> NumberList3 { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
  }
");
        Assert.That(diagnostic, Is.Empty);
    }

    [Test]
    public void Diagnostic_015()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDrirectional)]
List<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
}
");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CollectionUnknownType.Id), Is.True);
    }
    
    [Test]
    public void Diagnostic_016()
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
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CollectionUnknownMode.Id), Is.True);
    }  
    
    [Test]
    public void Diagnostic_007_DuplicatedWithCollection()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDrirectional, 1)]
INexusList<int> NumberList { get; }
[NexusCollectionAttribute(NexusCollectionMode.BiDrirectional, 1)]
INexusList<int> NumberList { get; }
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
}
");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id), Is.True);
    }
    
    [Test]
    public void Diagnostic_007_DuplicatedWithMethod()
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
[NexusCollectionAttribute(NexusCollectionMode.BiDrirectional, 1)]
INexusList<int> NumberList { get; }
[NexusMethod(1)] void Update1();
}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
}
");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id), Is.True);
    }
    
    [Test]
    public void Diagnostic_017()
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
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CollectionAttributeMissing.Id), Is.True);
    }
}

