using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using NexNet.Serialization;

namespace NexNetBenchmarks;

/// <summary>
/// In-process serialize and deserialize benchmarks of NexNet's MessagePack serializer over representative payloads.
/// </summary>
[MemoryDiagnoser]
public class SerializerBenchmarks
{
    public enum Payload
    {
        Int32,
        AsciiShort,
        AsciiLong,
        Unicode,
        Poco,
        NestedGraph,
        Doubles16,
        Doubles1K,
        Doubles64K,
        PersonList100,
        Union,
    }

    [Params(Payload.Int32, Payload.AsciiShort, Payload.AsciiLong, Payload.Unicode, Payload.Poco, Payload.NestedGraph,
        Payload.Doubles16, Payload.Doubles1K, Payload.Doubles64K, Payload.PersonList100, Payload.Union)]
    public Payload Kind { get; set; }

    private readonly ArrayBufferWriter<byte> _buffer = new(1024 * 1024);
    private Action<IBufferWriter<byte>> _nexusWrite = null!;
    private Action _nexusRead = null!;

    [GlobalSetup]
    public void Setup()
    {
        switch (Kind)
        {
            case Payload.Int32: Configure(123456); break;
            case Payload.AsciiShort: Configure("hello world"); break;
            case Payload.AsciiLong: Configure(new string('x', 2000)); break;
            case Payload.Unicode: Configure(string.Concat(Enumerable.Repeat("日本語テキスト ü ", 100))); break;
            case Payload.Poco: Configure(BenchPerson.Create(42)); break;
            case Payload.NestedGraph: Configure(BenchOrder.Create(7)); break;
            case Payload.Doubles16: Configure(Enumerable.Range(0, 16).Select(i => i * 0.5).ToArray()); break;
            case Payload.Doubles1K: Configure(Enumerable.Range(0, 1024).Select(i => i * 0.5).ToArray()); break;
            case Payload.Doubles64K: Configure(Enumerable.Range(0, 65536).Select(i => i * 0.5).ToArray()); break;
            case Payload.PersonList100: Configure(Enumerable.Range(0, 100).Select(BenchPerson.Create).ToList()); break;
            case Payload.Union: Configure<IBenchShape>(new BenchSquare { Side = 3.5 }); break;
        }
    }

    private void Configure<T>(T value)
    {
        var formatter = NexusFormatterRegistry.Get<T>();
        _nexusWrite = output =>
        {
            var writer = new MsgPackWriter(output);
            formatter.Serialize(ref writer, value);
            writer.Flush();
        };

        var nexusBytes = Serialize(_nexusWrite);
        _nexusRead = () =>
        {
            var reader = new MsgPackReader(nexusBytes, NexusSerializerOptions.Untrusted);
            T? result = default;
            formatter.Deserialize(ref reader, ref result);
        };

        WireSizes[Kind] = nexusBytes.Length;
    }

    /// <summary>
    /// Bytes on the wire per payload.
    /// </summary>
    public static readonly Dictionary<Payload, int> WireSizes = new();

    private static byte[] Serialize(Action<IBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    [Benchmark(Baseline = true)]
    public void NexNet_Serialize()
    {
        _buffer.ResetWrittenCount();
        _nexusWrite(_buffer);
    }

    [Benchmark]
    public void NexNet_Deserialize() => _nexusRead();

    /// <summary>
    /// Prints wire sizes for every payload (run with --sizes).
    /// </summary>
    public static void PrintWireSizes()
    {
        var bench = new SerializerBenchmarks();
        Console.WriteLine("| Payload | NexNet (B) |");
        Console.WriteLine("|---|---|");
        foreach (var kind in Enum.GetValues<Payload>())
        {
            bench.Kind = kind;
            bench.Setup();
            Console.WriteLine($"| {kind} | {WireSizes[kind]} |");
        }
    }
}
