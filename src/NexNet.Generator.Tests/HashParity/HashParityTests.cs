using NexNet.Generator.Ir;
using TUnit.Assertions.Enums;

namespace NexNet.Generator.Tests.HashParity;

/// <summary>
/// The handshake constants the IR gives foreign targets (<c>ExportIr.Handshake</c>, <c>ExportIr.ClientAcceptedIds</c>)
/// against what a compiled C# client of the same interface pair computes at runtime (<c>MethodHash</c>,
/// <c>VersionHashTable</c>, <c>VersionMethodHashSet</c> of its nexus and proxy), and against what the compiled server
/// accepts. A foreign client that sends the IR values is then indistinguishable from the C# client on the wire.
/// </summary>
public class HashParityTests
{
    private static HashParityRun Fixture(string fixture) => fixture switch
    {
        "Plain" => HashParityRunner.Run(fixture, HashParityFixtures.Plain()),
        "Versioned" => HashParityRunner.Run(fixture, HashParityFixtures.Versioned()),
        "VersionedV1Export" => HashParityRunner.Run(fixture, HashParityFixtures.VersionedV1Export()),
        "Rich" => HashParityRunner.Run(fixture, HashParityFixtures.Rich()),
        _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, null),
    };

    [Test]
    [Arguments("Plain", "ServerNexus", "ClientNexus", "ServerNexus")]
    [Arguments("Versioned", "VersionedServerNexusV2", "VersionedClientNexusV2", "VersionedServerNexusV2")]
    [Arguments("Versioned", "VersionedServerNexusV1_1", "VersionedClientNexusV1_1", "VersionedServerNexusV1_1")]
    [Arguments("Versioned", "NonVersionedServerNexus", "NonVersionedClientNexus", "NonVersionedServerNexus")]
    [Arguments("VersionedV1Export", "VersionedServerNexusV1", "VersionedClientNexusV1", "VersionedServerNexusV2")]
    [Arguments("Rich", "RichServerNexus", "RichClientNexus", "RichServerNexus")]
    public async Task IrHandshake_EqualsTheCompiledClient(string fixture, string export, string client, string server)
    {
        var run = Fixture(fixture);
        var ir = run.Export(export);
        var csharp = run.Nexus(client);
        await Assert.That(csharp.IsServer).IsFalse();

        // The greeting: Version, ServerNexusHash, ClientNexusHash.
        var greeting = csharp.ClientGreeting();
        await Assert.That(ir.Handshake.Version).IsEqualTo(greeting.Version);
        await Assert.That(ir.Handshake.ServerNexusHash).IsEqualTo(greeting.ServerNexusHash);
        await Assert.That(ir.Handshake.ClientNexusHash).IsEqualTo(greeting.ClientNexusHash);

        // The API hashes of the two interfaces are the proxy's and the nexus's MethodHash.
        await Assert.That(ir.ServerApi.Hash).IsEqualTo(csharp.Proxy.MethodHash);
        await Assert.That(ir.ClientApi.Hash).IsEqualTo(csharp.Nexus.MethodHash);

        // The proxy's version table holds the server API's last version only.
        var lastVersion = ir.ServerApi.Versions.Length == 0 ? null : ir.ServerApi.Versions[ir.ServerApi.Versions.Length - 1];
        var expectedTable = lastVersion == null
            ? Array.Empty<KeyValuePair<string, int>>()
            : new[] { new KeyValuePair<string, int>(lastVersion.Version, lastVersion.Hash) };
        await Assert.That(csharp.Proxy.VersionHashTable).IsEquivalentTo(expectedTable, CollectionOrdering.Matching);

        // The client has no versions: incoming invocations are checked against its own API hash.
        await Assert.That(csharp.Nexus.VersionHashTable).IsEmpty();
        await Assert.That(ir.ClientAcceptedIds.All(a => a.Hash == csharp.Nexus.MethodHash)).IsTrue();

        // The accepted (hash, id) pairs are the client nexus's VersionMethodHashSet.
        var accepted = ir.ClientAcceptedIds.Select(a => VmHash(a.Hash, a.Id)).Order().ToArray();
        await Assert.That(accepted).IsEquivalentTo(csharp.Nexus.VersionMethodHashSet, CollectionOrdering.Matching);
        await Assert.That(ir.ClientAcceptedIds.Select(a => a.Id).ToArray()).IsOrderedBy(id => id);
    }

    [Test]
    [Arguments("Plain", "ServerNexus", "ServerNexus")]
    [Arguments("Versioned", "VersionedServerNexusV2", "VersionedServerNexusV2")]
    [Arguments("Versioned", "VersionedServerNexusV1_1", "VersionedServerNexusV1_1")]
    [Arguments("Versioned", "NonVersionedServerNexus", "NonVersionedServerNexus")]
    [Arguments("VersionedV1Export", "VersionedServerNexusV1", "VersionedServerNexusV2")]
    [Arguments("VersionedV1Export", "VersionedServerNexusV1", "VersionedServerNexusV1_1")]
    [Arguments("Rich", "RichServerNexus", "RichServerNexus")]
    public async Task IrHandshake_IsAcceptedByTheCompiledServer(string fixture, string export, string server)
    {
        var run = Fixture(fixture);
        var ir = run.Export(export);
        var csharp = run.Nexus(server);
        await Assert.That(csharp.IsServer).IsTrue();

        // The server's greeting check passes and the session settles on the greeting's hash.
        var versionHash = csharp.AcceptGreeting(ir.Handshake);
        await Assert.That(versionHash).IsEqualTo(ir.Handshake.ServerNexusHash);

        // The server greeting carries the server proxy's MethodHash, which the client compares with its own API hash.
        await Assert.That(csharp.Proxy.MethodHash).IsEqualTo(ir.Handshake.ClientNexusHash);

        // Every method and collection of the exported server API is invocable on the negotiated version.
        var invocable = ir.ServerApi.Methods.Select(m => m.Id)
            .Concat(ir.ServerApi.Collections.Select(c => c.Id))
            .Select(id => VmHash(versionHash!.Value, id))
            .ToArray();
        await Assert.That(invocable).IsNotEmpty();
        await Assert.That(invocable.Except(csharp.Nexus.VersionMethodHashSet).ToArray()).IsEmpty();
    }

    [Test]
    public async Task Fixtures_CoverTheIntendedShapes()
    {
        // Guards the fixtures: each feature the parity tests claim to cover is present in the exported APIs.
        var rich = Fixture("Rich").Export("RichServerNexus");
        await Assert.That(rich.ServerApi.Collections.Length).IsEqualTo(2);
        await Assert.That(rich.ServerApi.Methods.Any(m => m.Id == 50)).IsTrue();
        await Assert.That(rich.ClientApi.Methods.Any(m => m.Id == 9)).IsTrue();
        await Assert.That(Fixture("Rich").Ir.Types.Any(t => t.Id == "Rich.Shape")).IsTrue();

        var versioned = Fixture("Versioned").Export("VersionedServerNexusV2");
        await Assert.That(versioned.Handshake.Version).IsEqualTo("v1.2");
        await Assert.That(Fixture("Versioned").Export("NonVersionedServerNexus").Handshake.Version).IsNull();

        var v1 = Fixture("VersionedV1Export");
        await Assert.That(v1.Ir.Exports.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] { "VersionedServerNexusV1" }, CollectionOrdering.Matching);
        await Assert.That(v1.Export("VersionedServerNexusV1").Handshake.Version).IsEqualTo("v1.0");
    }

    private static long VmHash(int hash, ushort id) => ((long)hash << 16) | id;
}
