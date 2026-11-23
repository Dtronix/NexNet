using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Invocation;
using NexNet.Pipes;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Schema;

// The nexus pair the schema decoder tests capture: one method per kind of value the decoder walks, a raw pipe, a typed
// channel, a synchronized list, a cancellable method and a call from the server to the client.

public enum DecodeColor : short
{
    Red = 1,
    Green = 2,
    Blue = -3,
}

[Flags]
public enum DecodeFlags : byte
{
    None = 0,
    A = 1,
    B = 2,
}

[NexusObject]
[NexusUnion<DecodeCircle>(0)]
[NexusUnion<DecodeSquare>(1)]
public abstract class DecodeShape
{
}

[NexusObject]
public sealed class DecodeCircle : DecodeShape
{
    [NexusKey(0)] public double Radius { get; set; }
}

/// <summary>Key 1 is unused: nil on the wire.</summary>
[NexusObject]
public sealed class DecodeSquare : DecodeShape
{
    [NexusKey(0)] public int Side { get; set; }
    [NexusKey(2)] public string? Label { get; set; }
}

[NexusObject]
public sealed class DecodeItem
{
    [NexusKey(0)] public int Id { get; set; }
    [NexusKey(1)] public string? Name { get; set; }
    [NexusKey(2)] public DecodeShape? Shape { get; set; }
    [NexusKey(3)] public List<string>? Tags { get; set; }
    [NexusKey(4)] public int[]? Values { get; set; }
    [NexusKey(5)] public DecodeColor Color { get; set; }
    [NexusKey(6)] public Guid Key { get; set; }
    [NexusKey(7)] public DecodeFlags Flags { get; set; }
}

[NexusObject]
public sealed class DecodeBox<T>
{
    [NexusKey(0)] public T Value { get; set; } = default!;
    [NexusKey(1)] public List<T>? More { get; set; }
}

public interface IDecodeServer
{
    ValueTask<long> Add(int a, long b, byte c);
    ValueTask<string?> Describe(string? text, double ratio, bool flag, DateTime when, decimal price);
    ValueTask<DecodeItem> Store(DecodeItem item);
    ValueTask<DecodeShape?> Reshape(DecodeShape? shape);
    ValueTask<Dictionary<string, int>> Count(List<string> words, int[] numbers, HashSet<string> set);
    ValueTask<int?> Maybe(int? value, DecodeColor? color);
    ValueTask<(int Id, string Name)> Pair((int, string) pair, KeyValuePair<string, int> entry);
    ValueTask<DecodeBox<DecodeItem>> Wrap(DecodeBox<int> box);
    ValueTask Upload(int tag, INexusDuplexPipe pipe);
    ValueTask Stream(INexusDuplexChannel<DecodeItem> channel);
    ValueTask<int> Wait(int milliseconds, CancellationToken cancellationToken);
    ValueTask Fail(string message);

    [NexusCollection(NexusCollectionMode.BiDirectional)]
    INexusList<DecodeItem> Items { get; }
}

public interface IDecodeClient
{
    ValueTask<string> Greet(DecodeItem item);
}

[Nexus<IDecodeServer, IDecodeClient>(NexusType = NexusType.Server)]
public partial class DecodeServerNexus
{
    /// <summary>Bytes read from each Upload pipe, in order.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<int> Uploaded = new();

    /// <summary>Items read from each Stream channel.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<DecodeItem> Streamed = new();

    public ValueTask<long> Add(int a, long b, byte c) => new(a + b + c);

    public ValueTask<string?> Describe(string? text, double ratio, bool flag, DateTime when, decimal price)
        => new($"{text}:{ratio}:{flag}:{when.Ticks}:{price}");

    public async ValueTask<DecodeItem> Store(DecodeItem item)
    {
        // A call from the server to the client: decoded with the client API.
        var greeting = await Context.Clients.Caller.Greet(item);
        return new DecodeItem { Id = item.Id + 1, Name = greeting, Shape = item.Shape, Color = DecodeColor.Green };
    }

    public ValueTask<DecodeShape?> Reshape(DecodeShape? shape) => new(shape switch
    {
        DecodeCircle circle => new DecodeSquare { Side = (int)circle.Radius, Label = "from circle" },
        _ => null,
    });

    public ValueTask<Dictionary<string, int>> Count(List<string> words, int[] numbers, HashSet<string> set)
        => new(words.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count() + numbers.Length + set.Count));

    public ValueTask<int?> Maybe(int? value, DecodeColor? color) => new(value is null ? null : value + (int)(color ?? 0));

    public ValueTask<(int Id, string Name)> Pair((int, string) pair, KeyValuePair<string, int> entry)
        => new((pair.Item1 + entry.Value, pair.Item2 + entry.Key));

    public ValueTask<DecodeBox<DecodeItem>> Wrap(DecodeBox<int> box)
        => new(new DecodeBox<DecodeItem> { Value = new DecodeItem { Id = box.Value }, More = [new DecodeItem { Id = -1 }] });

    public async ValueTask Upload(int tag, INexusDuplexPipe pipe)
    {
        var total = 0;
        while (true)
        {
            var read = await pipe.Input.ReadAsync();
            total += (int)read.Buffer.Length;
            pipe.Input.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted || read.IsCanceled)
                break;
        }

        Uploaded.Enqueue(total);
    }

    public async ValueTask Stream(INexusDuplexChannel<DecodeItem> channel)
    {
        foreach (var item in await channel.ReadUntilComplete())
            Streamed.Enqueue(item);
    }

    public async ValueTask<int> Wait(int milliseconds, CancellationToken cancellationToken)
    {
        await Task.Delay(milliseconds, cancellationToken);
        return milliseconds;
    }

    public ValueTask Fail(string message) => throw new InvalidOperationException(message);

    // Sends the exception detail (code, type, message) so the result carries a value to decode.
    protected override NexusRemoteError? OnInvocationException(Exception exception, int methodId) => new(42, exception.Message);
}

[Nexus<IDecodeClient, IDecodeServer>(NexusType = NexusType.Client)]
public partial class DecodeClientNexus
{
    public ValueTask<string> Greet(DecodeItem item) => new("hello " + item.Name);
}
