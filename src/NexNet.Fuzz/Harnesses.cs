using System.Buffers;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;

[assembly: NexusSerializable<NexNet.Fuzz.FuzzEnvelope<NexNet.Fuzz.FuzzOrder>>]

namespace NexNet.Fuzz;

/// <summary>
/// Fuzz harnesses. Each takes arbitrary bytes and must only ever throw <see cref="NexusSerializationException"/>
/// (expected rejection of malformed input). Any other exception, hang or crash is a bug.
/// </summary>
public static class Harnesses
{
    public static readonly IReadOnlyDictionary<string, Action<byte[]>> All = new Dictionary<string, Action<byte[]>>
    {
        ["reader"] = Reader,
        ["messages"] = ProtocolMessages,
        ["channel"] = Channel,
        ["formatters"] = GeneratedFormatters,
        ["builtins"] = BuiltInFormatters,
        ["session"] = SessionHarness.Run,
    };

    /// <summary>
    /// TrySkip plus a generic value walk over arbitrary input.
    /// </summary>
    public static void Reader(byte[] data)
    {
        Expect(() =>
        {
            var reader = new MsgPackReader(data);
            var probe = reader;
            if (probe.TrySkip() && probe.Consumed > data.Length)
                throw new InvalidOperationException("TrySkip consumed more than the input.");

            while (!reader.End)
            {
                Walk(ref reader, 0);
            }
        });
    }

    private static void Walk(ref MsgPackReader reader, int depth)
    {
        if (depth > 64)
            throw new NexusSerializationException("depth");

        var code = reader.PeekCode();
        switch (code)
        {
            case <= MsgPackCode.MaxFixPositive or >= MsgPackCode.MinFixNegative
                or MsgPackCode.Int8 or MsgPackCode.Int16 or MsgPackCode.Int32 or MsgPackCode.Int64
                or MsgPackCode.UInt8 or MsgPackCode.UInt16 or MsgPackCode.UInt32:
                reader.ReadInt64();
                break;
            case MsgPackCode.UInt64:
                reader.ReadUInt64();
                break;
            case MsgPackCode.Float32:
            case MsgPackCode.Float64:
                reader.ReadDouble();
                break;
            case MsgPackCode.Nil:
                reader.ReadNil();
                break;
            case MsgPackCode.True:
            case MsgPackCode.False:
                reader.ReadBoolean();
                break;
            case >= MsgPackCode.MinFixStr and <= MsgPackCode.MaxFixStr or MsgPackCode.Str8 or MsgPackCode.Str16 or MsgPackCode.Str32:
                reader.ReadString();
                break;
            case MsgPackCode.Bin8 or MsgPackCode.Bin16 or MsgPackCode.Bin32:
                reader.ReadBinary();
                break;
            case >= MsgPackCode.MinFixArray and <= MsgPackCode.MaxFixArray or MsgPackCode.Array16 or MsgPackCode.Array32:
            {
                var count = reader.ReadArrayHeader();
                for (var i = 0; i < count; i++)
                    Walk(ref reader, depth + 1);
                break;
            }
            case >= MsgPackCode.MinFixMap and <= MsgPackCode.MaxFixMap or MsgPackCode.Map16 or MsgPackCode.Map32:
            {
                var count = reader.ReadMapHeader();
                for (var i = 0; i < count * 2; i++)
                    Walk(ref reader, depth + 1);
                break;
            }
            default:
                reader.Skip();
                break;
        }
    }

    /// <summary>
    /// Each protocol message's hand-written deserializer with the strict trailing-byte check.
    /// </summary>
    public static void ProtocolMessages(byte[] data)
    {
        Message<ClientGreetingMessage>(data);
        Message<ClientGreetingReconnectionMessage>(data);
        Message<ServerGreetingMessage>(data);
        Message<InvocationMessage>(data);
        Message<InvocationResultMessage>(data);
        Message<InvocationCancellationMessage>(data);
        Message<DuplexPipeUpdateStateMessage>(data);
    }

    private static void Message<T>(byte[] data)
        where T : class, IMessageBase, new()
    {
        Expect(() =>
        {
            var message = new T();
            var reader = new MsgPackReader(data);
            message.Deserialize(ref reader);
            if (!reader.End)
                throw new NexusSerializationException("trailing");
        });
    }

    /// <summary>
    /// The channel reader over the input split at arbitrary chunk boundaries.
    /// </summary>
    public static void Channel(byte[] data)
    {
        Expect(() =>
        {
            var pipeReader = new NexusPipeReader(new FuzzPipeStateManager(), null, true, 0, 0, 0);
            var reader = new NexusChannelReader<FuzzOrder>(pipeReader);
            var chunk = data.Length == 0 ? 1 : data[0] % 7 + 1;
            var list = new List<FuzzOrder>();
            for (var i = 0; i < data.Length; i += chunk)
            {
                var writer = BufferWriter<byte>.Create();
                writer.Write(data.AsSpan(i, Math.Min(chunk, data.Length - i)));
                using (var buffer = writer.Flush())
                    pipeReader.BufferData(buffer).AsTask().GetAwaiter().GetResult();

                // Read whatever is complete; an incomplete item makes the reader wait, so bound it with a short timeout.
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
                list.Clear();
                reader.ReadAsync(list, null, cts.Token).AsTask().GetAwaiter().GetResult();
            }
        });
    }

    /// <summary>
    /// Generated formatters for a representative DTO graph (union, generics, collections).
    /// </summary>
    public static void GeneratedFormatters(byte[] data)
    {
        Formatter<FuzzOrder>(data);
        Formatter<IFuzzShape>(data);
        Formatter<FuzzEnvelope<FuzzOrder>>(data);
    }

    /// <summary>
    /// Built-in formatters for common payload types.
    /// </summary>
    public static void BuiltInFormatters(byte[] data)
    {
        Formatter<string[]>(data);
        Formatter<Dictionary<string, int>>(data);
        Formatter<int[]>(data);
        Formatter<double[]>(data);
        Formatter<Guid>(data);
        Formatter<decimal>(data);
        Formatter<DateTimeOffset>(data);
        Formatter<List<string>>(data);
    }

    private static void Formatter<T>(byte[] data)
    {
        Expect(() =>
        {
            var reader = new MsgPackReader(data);
            T? value = default;
            NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        });
    }

    private static void Expect(Action action)
    {
        try
        {
            action();
        }
        catch (NexusSerializationException)
        {
            // Expected rejection of malformed input.
        }
        catch (OperationCanceledException)
        {
            // The channel harness waits for more data on incomplete input; a timeout is not a defect.
        }
    }
}

internal sealed class FuzzPipeStateManager : IPipeStateManager
{
    public ushort Id => 0;
    public ValueTask NotifyState() => default;
    public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
    {
        CurrentState |= updatedState;
        return true;
    }

    public NexusDuplexPipe.State CurrentState { get; private set; } = NexusDuplexPipe.State.Ready;
}

[NexusObject]
public class FuzzOrder
{
    [NexusKey(0)] public int Id { get; set; }
    [NexusKey(1)] public string? Customer { get; set; }
    [NexusKey(2)] public List<FuzzLine>? Lines { get; set; }
    [NexusKey(3)] public Dictionary<string, double>? Totals { get; set; }
    [NexusKey(4)] public IFuzzShape? Shape { get; set; }
    [NexusKey(5)] public long[]? Codes { get; set; }
}

[NexusObject]
public struct FuzzLine
{
    [NexusKey(0)] public int Quantity { get; set; }
    [NexusKey(1)] public decimal Price { get; set; }
}

[NexusObject]
[NexusUnion<FuzzCircle>(0)]
[NexusUnion<FuzzSquare>(1)]
public interface IFuzzShape { }

[NexusObject]
public class FuzzCircle : IFuzzShape { [NexusKey(0)] public double Radius { get; set; } }

[NexusObject]
public class FuzzSquare : IFuzzShape { [NexusKey(0)] public FuzzOrder? Nested { get; set; } }

[NexusObject]
public class FuzzEnvelope<T> { [NexusKey(0)] public T? Value { get; set; } [NexusKey(1)] public List<T>? Items { get; set; } }
