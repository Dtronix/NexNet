using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

class VersioningTests
{
    
    [Test]
    public void CompilesServerNexusAcrossMultipleInterfaces()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus {  }
[NexusVersion(Version = "V4")]
partial interface IServerNexusV4 : IServerNexusV3 { void Update3(string[]? val); }
[NexusVersion(Version = "V3")]
partial interface IServerNexusV3 : IServerNexusV2, IServerNexusV2_2 { void Update2(string[]? val); }
[NexusVersion(Version = "V2")]
partial interface IServerNexusV2 : IServerNexus { void Update1(string[]? val); }
[NexusVersion(Version = "V2.1")]
partial interface IServerNexusV2_1 : IServerNexusV2 { void Update1_1(string[]? val); }
[NexusVersion(Version = "V2.2")]
partial interface IServerNexusV2_2 : IServerNexusV2_1 { void Update1_2(string[]? val); }
partial interface IServerNexus { void UpdateBase(string[]? val); }

//[Nexus<IClientNexus, IServerNexusV2_2>(NexusType = NexusType.Client)]
//partial class ClientNexus { }

[Nexus<IServerNexusV4, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update1(string[]? val){ }
    public void Update1_1(string[]? val){ }
    public void Update1_2(string[]? val){ }
    public void Update2(string[]? val){ }
    public void Update3(string[]? val){ }
    public void UpdateBase(string[]? val){ }

}
""");
        Assert.That(diagnostic, Is.Empty);
    }
    
    [Test]
    public void MemoryPackableObjects()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using System;
using System.Collections.Generic;
using NexNet;
using MemoryPack;
namespace NexNetDemo;
[MemoryPackable]
partial class DataObject { 
    public string Value1 { get; set; } 
    public int Value2 { get; set; } 
}
partial interface IClientNexus { }
[NexusVersion(Version = "v2", HashLock=-1605840564)]
partial interface IServerNexus { void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2); }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2) { }
}
""");
        Assert.That(diagnostic, Is.Empty);
    }
    
    [Test]
    public void VersionLock_MemoryPack_ObjectsWithSameContentsProduceSameHash()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using MemoryPack;
using System;
namespace NexNetDemo;
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class Message {
    [MemoryPackOrder(0)] public int Version { get; set; }
    [MemoryPackOrder(1)] public int TotalValues { get; set; }
}
internal partial class Message2 {
    [MemoryPackOrder(0)] public int VersionDiff { get; set; }
    [MemoryPackOrder(1)] public int TotalValuesDiff { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = 1080457006)]
partial interface IServerNexus { 
    void Update(Message data);
}
[NexusVersion(Version = "v1", HashLock = -1485727146)]
partial interface IServerNexus2 {
    void Update(System.Tuple<Message2> data);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus { 
    public void Update(Message data) { }
}
[Nexus<IServerNexus2, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus2 { 
    public void Update(System.Tuple<Message2> data) { }
}
""", minDiagnostic:DiagnosticSeverity.Warning);
        Assert.That(diagnostic, Is.Empty);
    }


    
    [Test]
    public void MemoryPackable_Interface()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using MemoryPack;
namespace NexNetDemo;
[MemoryPackable]
[MemoryPackUnion(1, typeof(VersionMessage))]         
[MemoryPackUnion(0, typeof(ValuesMessage))]        
internal partial interface IMessageV1 { 
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class VersionMessage : IMessageV1 {
    [MemoryPackOrder(0)] public int Version { get; set; }
    [MemoryPackOrder(1)] public int TotalValues { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValuesMessage : IMessageV1 {
    [MemoryPackOrder(0)] public byte[] Values { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1")]
partial interface IServerNexus { void Update(IMessageV1 data); }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(IMessageV1 data) { }
}
""", minDiagnostic:DiagnosticSeverity.Error);
        Assert.That(diagnostic, Is.Empty);
    }
    
    [Test]
    public void MemoryPackable_NestedCreation()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using MemoryPack;
using System;
namespace NexNetDemo;
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class Message {
    [MemoryPackOrder(0)] public VersionMessage[] Messages { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class VersionMessage {
    [MemoryPackOrder(0)] public int Version { get; set; }
    [MemoryPackOrder(1)] public int TotalValues { get; set; }
    [MemoryPackOrder(2)] public ValuesMessage Values { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValuesMessage {
    [MemoryPackOrder(0)] public byte[] Values { get; set; }
    [MemoryPackOrder(1)] public ValueObjects ValueObjects { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValueObjects {
    [MemoryPackOrder(0)] public string[] Values { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = -764721642)]
partial interface IServerNexus { void Update(ValueTuple<Message> data); }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(ValueTuple<Message> data) { }
}
""", minDiagnostic:DiagnosticSeverity.Warning);
        Assert.That(diagnostic, Is.Empty);
    }
    
    [Test]
    public void HashLockFailsOnMemberChange()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using System;
using System.Collections.Generic;
using NexNet;
using MemoryPack;
namespace NexNetDemo;
[MemoryPackable]
partial class DataObject { 
    public string Value1 { get; set; } 
    public short Value2 { get; set; } 
}
partial interface IClientNexus { }
[NexusVersion(Version = "v2", HashLock=-1605840564)]
partial interface IServerNexus { void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2); }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2) { }
}
""", minDiagnostic: DiagnosticSeverity.Error);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.VersionHashLockMismatch.Id), Is.True);
    }
    
    [Test]
    public void HashLockFailsOnNextedMemberChange()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using MemoryPack;
using System;
namespace NexNetDemo;
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class Message {
    [MemoryPackOrder(0)] public VersionMessage[] Messages { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class VersionMessage {
    [MemoryPackOrder(0)] public int Version { get; set; }
    [MemoryPackOrder(1)] public int TotalValues { get; set; }
    [MemoryPackOrder(2)] public ValuesMessage Values { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValuesMessage {
    [MemoryPackOrder(0)] public int[] Values { get; set; }
    [MemoryPackOrder(1)] public ValueObjects ValueObjects { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValueObjects {
    [MemoryPackOrder(0)] public string[] Values { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = -764721642)]
partial interface IServerNexus { void Update(ValueTuple<Message> data); }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(ValueTuple<Message> data) { }
}
""", minDiagnostic:DiagnosticSeverity.Error);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.VersionHashLockMismatch.Id), Is.True);
    }
    /*
    [Test]
    public void WarnsOnNoLockSet()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using System;
using System.Collections.Generic;
using NexNet;
using MemoryPack;
namespace NexNetDemo;
[MemoryPackable]
partial class DataObject { 
    public string Value1 { get; set; } 
    public int Value2 { get; set; } 
}
partial interface IClientNexus { }
[NexusVersion(Version = "v2")]
partial interface IServerNexus { void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2); }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2) { }
}
""", minDiagnostic: DiagnosticSeverity.Warning);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.VersionHashLockNotSet.Id), Is.True);
    }
    */

}

