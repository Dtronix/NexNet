using System;
using System.Collections.Generic;
using MemoryPack;
using NexNet.Serialization;

// Used only at runtime by SerializerBenchmarks (not in any nexus signature), so it must be declared.
[assembly: NexusSerializable<List<NexNetBenchmarks.BenchPerson>>]

namespace NexNetBenchmarks;

/// <summary>
/// Small POCO (5 members), annotated for both serializers so they can be compared in one process.
/// </summary>
[MemoryPackable]
[NexusObject]
public partial class BenchPerson
{
    [NexusKey(0)] public int Id { get; set; }
    [NexusKey(1)] public string? Name { get; set; }
    [NexusKey(2)] public double Score { get; set; }
    [NexusKey(3)] public bool Active { get; set; }
    [NexusKey(4)] public DateTime Created { get; set; }

    public static BenchPerson Create(int i) => new()
    {
        Id = i,
        Name = "Person " + i,
        Score = i * 1.5,
        Active = (i & 1) == 0,
        Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i)
    };
}

/// <summary>
/// Nested graph.
/// </summary>
[MemoryPackable]
[NexusObject]
public partial class BenchOrder
{
    [NexusKey(0)] public int Id { get; set; }
    [NexusKey(1)] public BenchPerson? Customer { get; set; }
    [NexusKey(2)] public List<BenchLine>? Lines { get; set; }
    [NexusKey(3)] public Dictionary<string, string>? Tags { get; set; }

    public static BenchOrder Create(int i) => new()
    {
        Id = i,
        Customer = BenchPerson.Create(i),
        Lines = [new BenchLine { Sku = "SKU-1", Quantity = 2, Price = 10.5 }, new BenchLine { Sku = "SKU-2", Quantity = 1, Price = 99.99 }],
        Tags = new Dictionary<string, string> { ["priority"] = "high", ["region"] = "us-east" }
    };
}

[MemoryPackable]
[NexusObject]
public partial class BenchLine
{
    [NexusKey(0)] public string? Sku { get; set; }
    [NexusKey(1)] public int Quantity { get; set; }
    [NexusKey(2)] public double Price { get; set; }
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(BenchCircle))]
[MemoryPackUnion(1, typeof(BenchSquare))]
[NexusObject]
[NexusUnion<BenchCircle>(0)]
[NexusUnion<BenchSquare>(1)]
public partial interface IBenchShape { }

[MemoryPackable]
[NexusObject]
public partial class BenchCircle : IBenchShape { [NexusKey(0)] public double Radius { get; set; } }

[MemoryPackable]
[NexusObject]
public partial class BenchSquare : IBenchShape { [NexusKey(0)] public double Side { get; set; } }
