using System.Text.RegularExpressions;
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
partial interface IServerNexusV4 : IServerNexusV3 { 
    [NexusMethod(400)]
    void Update3(string[]? val); 
}
[NexusVersion(Version = "V3")]
partial interface IServerNexusV3 : IServerNexusV2, IServerNexusV2_2 { 
    [NexusMethod(300)]
    void Update2(string[]? val); 
}
[NexusVersion(Version = "V2")]
partial interface IServerNexusV2 : IServerNexus { 
    [NexusMethod(200)]
    void Update1(string[]? val); 
}
[NexusVersion(Version = "V2.1")]
partial interface IServerNexusV2_1 : IServerNexusV2 {
    [NexusMethod(110)]
    void Update1_1(string[]? val); 
}
[NexusVersion(Version = "V2.2")]
partial interface IServerNexusV2_2 : IServerNexusV2_1 { 
    [NexusMethod(220)]
    void Update1_2(string[]? val); 
}
[NexusVersion(Version = "V1")]
partial interface IServerNexus { 
    [NexusMethod(100)]
    void UpdateBase(string[]? val);
}

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
    public void NexusObjects()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using System;
using System.Collections.Generic;
using NexNet;
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
partial class DataObject { 
    [NexusKey(0)] public string Value1 { get; set; } 
    [NexusKey(1)] public int Value2 { get; set; } 
}
partial interface IClientNexus { }
[NexusVersion(Version = "v2", HashLock = -1237929879)]
partial interface IServerNexus {
    [NexusMethod(100)]
    void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2) { }
}
""");
        Assert.That(diagnostic, Is.Empty);
    }
    
    [Test]
    public void VersionLock_ObjectsWithSameContentsProduceSameHash()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Serialization;
using System;
namespace NexNetDemo;
[NexusObject]
internal partial class Message {
    [NexusKey(0)] public int Version { get; set; }
    [NexusKey(1)] public int TotalValues { get; set; }
}
[NexusObject]
internal partial class Message2 {
    [NexusKey(0)] public int VersionDiff { get; set; }
    [NexusKey(1)] public int TotalValuesDiff { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = -1003302097)]
partial interface IServerNexus {
    [NexusMethod(100)]
    void Update(Message data);
}
[NexusVersion(Version = "v1", HashLock = -1003302097)]
partial interface IServerNexus2 {
    [NexusMethod(100)]
    void Update(Message2 data);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus { 
    public void Update(Message data) { }
}
[Nexus<IServerNexus2, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus2 { 
    public void Update(Message2 data) { }
}
""", minDiagnostic:DiagnosticSeverity.Warning);
        Assert.That(diagnostic, Is.Empty);
    }


    
    [Test]
    public void NexusUnion_Interface()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
[NexusUnion<VersionMessage>(1)]
[NexusUnion<ValuesMessage>(0)]
internal partial interface IMessageV1 { 
}
[NexusObject]
internal partial class VersionMessage : IMessageV1 {
    [NexusKey(0)] public int Version { get; set; }
    [NexusKey(1)] public int TotalValues { get; set; }
}
[NexusObject]
internal partial class ValuesMessage : IMessageV1 {
    [NexusKey(0)] public byte[] Values { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1")]
partial interface IServerNexus { 
    [NexusMethod(100)]
    void Update(IMessageV1 data);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(IMessageV1 data) { }
}
""", minDiagnostic:DiagnosticSeverity.Error);
        Assert.That(diagnostic, Is.Empty);
    }
    
    [Test]
    public void NexusObject_NestedCreation()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Serialization;
using System;
namespace NexNetDemo;
[NexusObject]
internal partial class Message {
    [NexusKey(0)] public VersionMessage[] Messages { get; set; }
}

[NexusObject]
internal partial class VersionMessage {
    [NexusKey(0)] public int Version { get; set; }
    [NexusKey(1)] public int TotalValues { get; set; }
    [NexusKey(2)] public ValuesMessage Values { get; set; }
}
[NexusObject]
internal partial class ValuesMessage {
    [NexusKey(0)] public byte[] Values { get; set; }
    [NexusKey(1)] public ValueObjects ValueObjects { get; set; }
}

[NexusObject]
internal partial class ValueObjects {
    [NexusKey(0)] public string[] Values { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = 366048920)]
partial interface IServerNexus {
    [NexusMethod(100)]
    void Update(ValueTuple<Message> data);
}
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
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
partial class DataObject { 
    [NexusKey(0)] public string Value1 { get; set; } 
    [NexusKey(1)] public short Value2 { get; set; } 
}
partial interface IClientNexus { }
[NexusVersion(Version = "v2", HashLock = -1237929879)]
partial interface IServerNexus {
    [NexusMethod(100)]
    void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2);
}
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
using NexNet.Serialization;
using System;
namespace NexNetDemo;
[NexusObject]
internal partial class Message {
    [NexusKey(0)] public VersionMessage[] Messages { get; set; }
}

[NexusObject]
internal partial class VersionMessage {
    [NexusKey(0)] public int Version { get; set; }
    [NexusKey(1)] public int TotalValues { get; set; }
    [NexusKey(2)] public ValuesMessage Values { get; set; }
}
[NexusObject]
internal partial class ValuesMessage {
    [NexusKey(0)] public int[] Values { get; set; }
    [NexusKey(1)] public ValueObjects ValueObjects { get; set; }
}

[NexusObject]
internal partial class ValueObjects {
    [NexusKey(0)] public string[] Values { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = 366048920)]
partial interface IServerNexus {
    [NexusMethod(100)]
    void Update(ValueTuple<Message> data);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { 
    public void Update(ValueTuple<Message> data) { }
}
""", minDiagnostic:DiagnosticSeverity.Error);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.VersionHashLockMismatch.Id), Is.True);
    }
    
    [Test]
    public void HashLockFailsOnReturnTypeMemberChange()
    {
        // The lock is the hash of the same source with Result.Value as an int.
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using System.Threading.Tasks;
using NexNet;
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
partial class Result {
    [NexusKey(0)] public long Value { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = -1860046668)]
partial interface IServerNexus {
    [NexusMethod(100)]
    ValueTask<Result> Get(int id);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
    public ValueTask<Result> Get(int id) => default;
}
""", minDiagnostic: DiagnosticSeverity.Error);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.VersionHashLockMismatch.Id), Is.True);
    }

    [Test]
    public void HashLockFailsOnCollectionItemMemberChange()
    {
        // The lock is the hash of the same source with Item.Value as an int.
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
partial class Item {
    [NexusKey(0)] public long Value { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = -445406768)]
partial interface IServerNexus {
    [NexusCollection(NexusCollectionMode.BiDirectional, 100)]
    INexusList<Item> Items { get; }
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { }
""", minDiagnostic: DiagnosticSeverity.Error);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.VersionHashLockMismatch.Id), Is.True);
    }

    [Test]
    public void HashLockFailsOnVoidToValueTask()
    {
        // The lock is the hash of the same source with "void Update();".
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using System.Threading.Tasks;
using NexNet;
namespace NexNetDemo;
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = -1852928916)]
partial interface IServerNexus {
    [NexusMethod(100)]
    ValueTask Update();
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
    public ValueTask Update() => default;
}
""", minDiagnostic: DiagnosticSeverity.Error);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.VersionHashLockMismatch.Id), Is.True);
    }

    [Test]
    public void HashLockKeepsOnTypeRename()
    {
        // The lock is the hash of the same source with the DTO named DataObject.
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
partial class Payload {
    [NexusKey(0)] public string Value1 { get; set; }
    [NexusKey(1)] public int Value2 { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = 1930900715)]
partial interface IServerNexus {
    [NexusMethod(100)]
    void Update(Payload data);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
    public void Update(Payload data) { }
}
""", minDiagnostic: DiagnosticSeverity.Error);
        Assert.That(diagnostic, Is.Empty);
    }

    [Test]
    public void DuplicateNexusMethodsAcrossMultipleInterfacesFails()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus {  }
[NexusVersion(Version = "V2", HashLock = 1248492465)]
partial interface IServerNexusV2 : IServerNexus { 
    [NexusMethod(100)]
    void Update2();
    
    [NexusMethod(201)]
    void Update3();
}
[NexusVersion(Version = "V1", HashLock = -1852928916)]
partial interface IServerNexus { 
    [NexusMethod(100)]
    void Update1(); 
}

[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update1(){ }
    public void Update2(){ }
    public void Update3(){ }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.DuplicatedMethodId.Id), Is.True);
    }
    
    [Test]
    public void HashLockIsEffectedByNexusMethodId()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus {  }
[NexusVersion(Version = "V2", HashLock = 1248492465)]
partial interface IServerNexusV2 : IServerNexus { 
    [NexusMethod(10)]
    void Update2();
    
    [NexusMethod(21)]
    void Update3();
}
[NexusVersion(Version = "V1", HashLock = -1852928916)]
partial interface IServerNexus { 
    [NexusMethod(10)]
    void Update1(); 
}

[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update1(){ }
    public void Update2(){ }
    public void Update3(){ }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.VersionHashLockMismatch.Id), Is.True);
    }
    
    [Test]
    public void EnsureAllInterfacesAreVersioningIfOneIs()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus {  }
partial interface IServerNexusV2 : IServerNexus { 
    [NexusMethod(200)]
    void Update2();
    
    [NexusMethod(201)]
    void Update3();
}
[NexusVersion(Version = "V1", HashLock = -1852928916)]
partial interface IServerNexus { 
    [NexusMethod(100)]
    void Update1(); 
}

[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update1(){ }
    public void Update2(){ }
    public void Update3(){ }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.AllInterfacesMustBeVersioning.Id), Is.True);
    }
    
    [Test]
    public void EnsureAllInterfacesAreVersioningIfOneIs2()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus {  }
[NexusVersion(Version = "V2", HashLock = -411948299)]
partial interface IServerNexusV2 : IServerNexus { 
    [NexusMethod(200)]
    void Update2();
    
    [NexusMethod(201)]
    void Update3();
}

partial interface IServerNexus { 
    [NexusMethod(100)]
    void Update1(); 
}

[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update1(){ }
    public void Update2(){ }
    public void Update3(){ }
}
""");
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.AllInterfacesMustBeVersioning.Id), Is.True);
    }
    
    [Test]
    public void AllMethodsIdsShallBeSetForVersioningNexuses()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus {  }
[NexusVersion(Version = "V2", HashLock = -1712946630)]
partial interface IServerNexusV2 : IServerNexus { 
    void Update2();
    
    [NexusMethod(100)]
    void Update3();
}

[NexusVersion(Version = "V2", HashLock = 1944564605)]
partial interface IServerNexus { 
    void Update1(); 
}

[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update1(){ }
    public void Update2(){ }
    public void Update3(){ }
}
""");
        Assert.That(diagnostic.Count(d => d.Id == DiagnosticDescriptors.AllMethodsIdsShallBeSetForVersioningNexuses.Id), Is.EqualTo(2));
    }
    
    [Test]
    public void AllMethodsIdsShallNotBe0ForVersioningNexuses()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System.Threading.Tasks;
namespace NexNetDemo;
partial interface IClientNexus {  }
[NexusVersion(Version = "V2", HashLock = 573670729)]
partial interface IServerNexusV2 : IServerNexus { 
    [NexusMethod(0)]
    void Update2();
    
    [NexusMethod()]
    void Update3();
}

[NexusVersion(Version = "V2", HashLock = 1944564605)]
partial interface IServerNexus { 
    [NexusMethod(MethodId = 0)]
    void Update1(); 
}

[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    public void Update1(){ }
    public void Update2(){ }
    public void Update3(){ }
}
""");
        Assert.That(diagnostic.Count(d => d.Id == DiagnosticDescriptors.AllMethodsIdsShallNotBe0ForVersioningNexuses.Id), Is.EqualTo(3));
    }

    [Test]
    public void HashLockIsDeterministicAcrossMultipleRuns()
    {
        var source = """
using System;
using System.Collections.Generic;
using NexNet;
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
partial class DataObject {
    [NexusKey(0)] public string Value1 { get; set; }
    [NexusKey(1)] public int Value2 { get; set; }
}
partial interface IClientNexus { }
[NexusVersion(Version = "v1", HashLock = 0)]
partial interface IServerNexus {
    [NexusMethod(100)]
    void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2);
}
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus {
    public void Update(DataObject data, List<ValueTuple<Tuple<DataObject, int>>> data2) { }
}
""";

        var hashes = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var diagnostics = CSharpGeneratorRunner.RunGenerator(source, minDiagnostic: DiagnosticSeverity.Error);
            var mismatch = diagnostics.FirstOrDefault(d => d.Id == DiagnosticDescriptors.VersionHashLockMismatch.Id);
            Assert.That(mismatch, Is.Not.Null, $"Expected NEXNET019 diagnostic on iteration {i}");

            var match = Regex.Match(mismatch!.GetMessage(), @"HashLock of '(-?\d+)'");
            Assert.That(match.Success, Is.True, $"Could not extract hash from diagnostic message: {mismatch.GetMessage()}");
            hashes.Add(match.Groups[1].Value);
        }

        Assert.That(hashes.Distinct().Count(), Is.EqualTo(1),
            $"Hash values were not deterministic across runs: {string.Join(", ", hashes)}");
        Assert.That(hashes[0], Is.Not.EqualTo("0"),
            "Extracted hash should not be zero");
    }

    /*
    [Test]
    public void WarnsOnNoLockSet()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using System;
using System.Collections.Generic;
using NexNet;
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
partial class DataObject { 
    [NexusKey(0)] public string Value1 { get; set; } 
    [NexusKey(1)] public int Value2 { get; set; } 
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

