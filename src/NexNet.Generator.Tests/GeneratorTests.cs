using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

class GeneratorTests
{
    [Test]
    public void MustBePartial_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustBePartial.Id), Is.True);
    }

    [Test]
    public void MustBePartial_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustBePartial.Id), Is.True);
    }

    [Test]
    public void MustNotBeAbstract_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial abstract class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustNotBeAbstractOrInterface.Id), Is.True);

    }

    [Test]
    public void MustNotBeAbstract_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
abstract partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.MustNotBeAbstractOrInterface.Id), Is.True);

    }

    [Test]
    public void NexusMustNotBeGeneric()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
abstract partial class ClientNexus<T> : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.NexusMustNotBeGeneric.Id), Is.True);

    }

    [Test]
    public void InvokeMethodCoreReservedMethodName()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus<T> : IClientNexus
{
    public void Update() { }

}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
   protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationRequestMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
{
}
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvokeMethodCoreReservedMethodName.Id), Is.True);

    }

    [Test]
    public void NexusMustNotBeGeneric_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus<T> : IClientNexus
{
    public void Update() { }

}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.NexusMustNotBeGeneric.Id), Is.True);

    }

    [Test]
    public void NexusMustNotBeGeneric_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }

}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus<T> : IServerNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.NexusMustNotBeGeneric.Id), Is.True);

    }




    [Test]
    public void CanGenerateClientOnly()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic, Is.Empty);

    }

    [Test]
    public void DuplicatedMethodId_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { 
[NexusMethod(1)] void Update0();
[NexusMethod(1)] void Update1();
}
partial interface IServerNexus { 

}
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id), Is.True);
    }


    [Test]
    public void DuplicatedMethodId_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { 

}
partial interface IServerNexus { 
    [NexusMethod(1)] void Update0();
    [NexusMethod(1)] void Update1();
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update() { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id), Is.True);
    }

    [Test]
    public void InvalidReturnValue_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { int Update(); }
partial interface IServerNexus { }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidReturnValue.Id), Is.True);
    }

    [Test]
    public void InvalidReturnValue_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus { int Update();  }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }

""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidReturnValue.Id), Is.True);
    }

    [Test]
    public void CompilesSimpleServerAndClient()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(); }
partial interface IServerNexus { void Update(string arg); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus
{
    public void Update() { }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{
    public void Update(string arg) { }
}
""");
        Assert.That(diagnostic, Is.Empty);
    }

    [Test]
    public void CompilesSimpleServerAndClientWithNullableReturn()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus { ValueTask<string?> Update(string[]? val); }
partial interface IServerNexus { }

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus
{

}
""");
        Assert.That(diagnostic, Is.Empty);
    }

    [Test]
    public void InvalidCancellationToken_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { ValueTask Update(CancellationToken ct, int val); }
partial interface IServerNexus { }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidCancellationToken.Id), Is.True);
    }

    [Test]
    public void InvalidCancellationToken_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus { ValueTask Update(CancellationToken ct, int val); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.InvalidCancellationToken.Id), Is.True);
    }


    [Test]
    public void CancellationTokenOnVoid_Client()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { void Update(CancellationToken ct); }
partial interface IServerNexus { }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id), Is.True);
    }

    [Test]
    public void CancellationTokenOnVoid_Server()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
partial interface IServerNexus {  void Update(CancellationToken ct); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus{ }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id), Is.True);
    }

    [Test]
    public void CompilesWhenInterfacesAndNexusesAreInSeparateNamespaces()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace InterfaceNameSpace1.One.Two
{
    partial interface IClientNexus { void Update(); }
}

namespace InterfaceNameSpace2.Three.Four
{
    partial interface IServerNexus { void Update(string arg); }
}
namespace HubNameSpaces1.Five.Six
{
    [Nexus<InterfaceNameSpace1.One.Two.IClientNexus, InterfaceNameSpace2.Three.Four.IServerNexus>(NexusType = NexusType.Client)]
    partial class ClientNexus : InterfaceNameSpace1.One.Two.IClientNexus
    {
        public void Update() { }
    }
}
namespace HubNameSpaces2.Seven.Eight
{
    [Nexus<InterfaceNameSpace2.Three.Four.IServerNexus, InterfaceNameSpace1.One.Two.IClientNexus>(NexusType = NexusType.Server)]
    partial class ServerNexus : InterfaceNameSpace2.Three.Four.IServerNexus
    {
        public void Update(string arg) { }
    }
}
""");
        Assert.That(diagnostic, Is.Empty);
    }
    
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
using NexNet;
using MemoryPack;
namespace NexNetDemo;
[MemoryPackable]
partial class DataObject { 
    public string Value1 { get; set; } 
    public int Value2 { get; set; } 
}
partial interface IClientNexus { }
[NexusVersion(Version = "v2", HashLock=-1549245336)]
partial interface IServerNexus {  void Update(DataObject data); }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    void Update(DataObject data) { }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id), Is.True);
    }
    
    [Test]
    public void VersionLock_MemoryPack_ObjectsWithSameContentsProduceSameHash()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using MemoryPack;
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
[NexusVersion(Version = "v1", HashLock = 1809729348)]
partial interface IServerNexus { 
    void Update(Message data);
}
[NexusVersion(Version = "v1", HashLock = 1809729348)]
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
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.VersionHashLockMismatch.Id), Is.False);
    }


    
    [Test]
    public void MemoryPackable_Interface()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using MemoryPack;
namespace NexNetDemo;
[MemoryPackable]
[MemoryPackUnion(0, typeof(VersionMessage))]         
[MemoryPackUnion(1, typeof(ValuesMessage))]        
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
""", minDiagnostic:DiagnosticSeverity.Warning);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id), Is.True);
    }
    
        [Test]
    public void MemoryPackable_NestedCreation()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using MemoryPack;
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
[NexusVersion(Version = "v1")]
partial interface IServerNexus { void Update(ValueTuple<Message> data); }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(ValueTuple<Message> data) { }
}

public class GenerateStructureHashAttribute : Attribute
{

}

""", minDiagnostic:DiagnosticSeverity.Warning);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id), Is.True);
    }
    
            [Test]
    public void TypeWalker()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator2("""
using NexNet;
using MemoryPack;
using System;
namespace NexNetDemo;
[GenerateStructureHash]
internal partial class Message {
    //[MemoryPackOrder(0)] public int Values { get; set; }
    //[MemoryPackOrder(10)] public Tuple<ValueTuple<VersionMessage>>?[] Values { get; set; }
    //[MemoryPackOrder(2)] public Tuple<ValueTuple<VersionMessage>>?[]? Values { get; set; }
    //[MemoryPackOrder(8)] public Tuple<ValueTuple<VersionMessage>>[]? Values { get; set; }
    //[MemoryPackOrder(4)] public Tuple<ValueTuple<Uri>> Values { get; set; }
    //[MemoryPackOrder(5)] public Tuple<ValueTuple<Rune>> Values { get; set; }
    //[MemoryPackOrder(6)] public ValueTuple<VersionMessage> Messages { get; set; }
    //[MemoryPackOrder(7)] public List<ValueObjects> Messages { get; set; }
    //[MemoryPackOrder(8)] public List<ValueTuple<List<Dictionary<byte, VersionMessage>, string?, string>,int>> Messages { get; set; }
    [MemoryPackOrder(1)] public Nullable<int> Messages22 { get; set; }
    [MemoryPackOrder(2)] public Nullable<int>[] Messages22 { get; set; }
    //[MemoryPackOrder(3)] public int[] Messages22 { get; set; }
    //[MemoryPackOrder(4)] public int?[] Messages22 { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class VersionMessage {
    [MemoryPackOrder(0)] public int Version { get; set; }
    [MemoryPackOrder(1)] public int TotalValues { get; set; }
    [MemoryPackOrder(2)] public ValuesMessage Values { get; set; }
    [MemoryPackOrder(2)] public DateTime Values { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValuesMessage {
    [MemoryPackOrder(0)] public byte[] Values { get; set; }
    [MemoryPackOrder(1)] public ValueObjects ValueObjects { get; set; }
    [MemoryPackOrder(2)] public VersionMessage Message { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValueObjects {
    [MemoryPackOrder(0)] public string[]? Values { get; set; }
}

public class GenerateStructureHashAttribute : Attribute
{

}

""", minDiagnostic:DiagnosticSeverity.Warning);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id), Is.True);
    }

}

