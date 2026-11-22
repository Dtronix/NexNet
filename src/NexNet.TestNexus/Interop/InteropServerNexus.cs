using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using NexNet.Invocation;
using NexNet.InteropServer;
using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.TestNexus.Interop;

/// <summary>
/// Server nexus of the interop tests. It echoes every value back unchanged and opts in to the Java and JavaScript
/// clients. Authentication goes to the interop server's token validator (<see cref="IInteropTokenValidatingNexus"/>);
/// without one every client is accepted with an anonymous identity.
/// </summary>
/// <remarks>
/// Authorization (<see cref="OnAuthorize"/>): the identity <see cref="AdminIdentity"/> holds every permission,
/// <see cref="ReaderIdentity"/> holds <see cref="InteropPermission.Read"/>, any other identity none.
/// </remarks>
[Nexus<IInteropServerNexus, IInteropClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.Java | NexusTargets.JavaScript)]
public partial class InteropServerNexus : IInteropTokenValidatingNexus
{
    /// <summary>Display name of an identity that holds every <see cref="InteropPermission"/>.</summary>
    public const string AdminIdentity = "admin";

    /// <summary>Display name of an identity that holds <see cref="InteropPermission.Read"/>.</summary>
    public const string ReaderIdentity = "reader";

    /// <summary>What <see cref="IInteropServerNexus.ReadSecret"/> returns.</summary>
    public const string Secret = "interop-secret";

    private readonly ConcurrentQueue<string> _notifications = new();

    /// <inheritdoc />
    public InteropTokenValidator? TokenValidator { get; set; }

    /// <summary>
    /// The bytes <see cref="IInteropServerNexus.CreateBytes"/> returns: byte <c>i</c> is <c>(i * 31 + seed) mod 256</c>.
    /// </summary>
    public static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
            data[i] = unchecked((byte)(i * 31 + seed));
        return data;
    }

    /// <summary>Lower-case hex SHA-256 of <paramref name="data"/>, as <see cref="IInteropServerNexus.HashBytes"/> returns it.</summary>
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// Formats an endpoint the way <see cref="IInteropServerNexus.GetEndpoint"/> returns it.
    /// </summary>
    public static string Describe(NexusEndpointInfo? endpoint)
        => endpoint == null
            ? ""
            : $"{endpoint.Name}|{endpoint.Transport}|{(endpoint.IsSecure ? "true" : "false")}|{endpoint.Path}";

    /// <inheritdoc />
    protected override ValueTask OnConnected(bool isReconnected)
    {
        _notifications.Clear();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? authenticationToken)
    {
        return TokenValidator == null
            ? ValueTask.FromResult<IIdentity?>(new DefaultIdentity())
            : TokenValidator(authenticationToken, Context.Endpoint);
    }

    /// <inheritdoc />
    protected override ValueTask<AuthorizeResult> OnAuthorize(
        ServerSessionContext<ClientProxy> context,
        int methodId,
        string methodName,
        ReadOnlyMemory<int> requiredPermissions)
    {
        var name = context.Identity?.DisplayName;
        if (name == AdminIdentity)
            return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);

        foreach (var permission in requiredPermissions.Span)
        {
            if (name != ReaderIdentity || permission != (int)InteropPermission.Read)
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
        }

        return new ValueTask<AuthorizeResult>(name == ReaderIdentity ? AuthorizeResult.Allowed : AuthorizeResult.Unauthorized);
    }

    // ----- Built-in types -----
    public ValueTask<bool> EchoBool(bool value) => new(value);
    public ValueTask<sbyte> EchoSByte(sbyte value) => new(value);
    public ValueTask<byte> EchoByte(byte value) => new(value);
    public ValueTask<short> EchoInt16(short value) => new(value);
    public ValueTask<ushort> EchoUInt16(ushort value) => new(value);
    public ValueTask<int> EchoInt32(int value) => new(value);
    public ValueTask<uint> EchoUInt32(uint value) => new(value);
    public ValueTask<long> EchoInt64(long value) => new(value);
    public ValueTask<ulong> EchoUInt64(ulong value) => new(value);
    public ValueTask<float> EchoSingle(float value) => new(value);
    public ValueTask<double> EchoDouble(double value) => new(value);
    public ValueTask<Half> EchoHalf(Half value) => new(value);
    public ValueTask<char> EchoChar(char value) => new(value);
    public ValueTask<decimal> EchoDecimal(decimal value) => new(value);
    public ValueTask<string?> EchoString(string? value) => new(value);
    public ValueTask<byte[]?> EchoBytes(byte[]? value) => new(value);

    // Argument memory may belong to the received message, so these copy it.
    public ValueTask<Memory<byte>> EchoMemory(Memory<byte> value) => new(value.ToArray());
    public ValueTask<ReadOnlyMemory<byte>> EchoReadOnlyMemory(ReadOnlyMemory<byte> value) => new(value.ToArray());
    public ValueTask<ReadOnlySequence<byte>> EchoReadOnlySequence(ReadOnlySequence<byte> value) => new(new ReadOnlySequence<byte>(value.ToArray()));
    public ValueTask<ArraySegment<byte>> EchoArraySegment(ArraySegment<byte> value) => new(new ArraySegment<byte>(value.ToArray()));

    public ValueTask<DateTime> EchoDateTime(DateTime value) => new(value);
    public ValueTask<DateTimeOffset> EchoDateTimeOffset(DateTimeOffset value) => new(value);
    public ValueTask<TimeSpan> EchoTimeSpan(TimeSpan value) => new(value);
    public ValueTask<DateOnly> EchoDateOnly(DateOnly value) => new(value);
    public ValueTask<TimeOnly> EchoTimeOnly(TimeOnly value) => new(value);
    public ValueTask<Guid> EchoGuid(Guid value) => new(value);
    public ValueTask<BigInteger> EchoBigInteger(BigInteger value) => new(value);
    public ValueTask<Uri?> EchoUri(Uri? value) => new(value);
    public ValueTask<Version?> EchoVersion(Version? value) => new(value);
    public ValueTask<(int, string?, double)> EchoValueTuple((int, string?, double) value) => new(value);
    public ValueTask<(int, long, string, bool, double, Guid, byte)> EchoValueTuple7((int, long, string, bool, double, Guid, byte) value) => new(value);
    public ValueTask<Tuple<int, string>?> EchoTuple(Tuple<int, string>? value) => new(value);
    public ValueTask<KeyValuePair<string, int>> EchoKeyValuePair(KeyValuePair<string, int> value) => new(value);

    // ----- Ext 78 primitive arrays -----
    public ValueTask<sbyte[]?> EchoSByteArray(sbyte[]? value) => new(value);
    public ValueTask<short[]?> EchoInt16Array(short[]? value) => new(value);
    public ValueTask<ushort[]?> EchoUInt16Array(ushort[]? value) => new(value);
    public ValueTask<int[]?> EchoInt32Array(int[]? value) => new(value);
    public ValueTask<uint[]?> EchoUInt32Array(uint[]? value) => new(value);
    public ValueTask<long[]?> EchoInt64Array(long[]? value) => new(value);
    public ValueTask<ulong[]?> EchoUInt64Array(ulong[]? value) => new(value);
    public ValueTask<float[]?> EchoSingleArray(float[]? value) => new(value);
    public ValueTask<double[]?> EchoDoubleArray(double[]? value) => new(value);
    public ValueTask<char[]?> EchoCharArray(char[]? value) => new(value);
    public ValueTask<Half[]?> EchoHalfArray(Half[]? value) => new(value);
    public ValueTask<List<int>?> EchoInt32List(List<int>? value) => new(value);
    public ValueTask<Memory<long>> EchoInt64Memory(Memory<long> value) => new(value.ToArray());
    public ValueTask<ReadOnlyMemory<double>> EchoDoubleReadOnlyMemory(ReadOnlyMemory<double> value) => new(value.ToArray());

    // ----- Containers -----
    public ValueTask<string[]?> EchoStringArray(string[]? value) => new(value);
    public ValueTask<List<string>?> EchoStringList(List<string>? value) => new(value);
    public ValueTask<IList<string>?> EchoStringIList(IList<string>? value) => new(value);
    public ValueTask<IReadOnlyList<InteropItem>?> EchoItemReadOnlyList(IReadOnlyList<InteropItem>? value) => new(value);
    public ValueTask<HashSet<string>?> EchoStringSet(HashSet<string>? value) => new(value);
    public ValueTask<HashSet<int>?> EchoInt32Set(HashSet<int>? value) => new(value);
    public ValueTask<Queue<int>?> EchoInt32Queue(Queue<int>? value) => new(value);
    public ValueTask<Stack<string>?> EchoStringStack(Stack<string>? value) => new(value);
    public ValueTask<Dictionary<string, string>?> EchoStringDictionary(Dictionary<string, string>? value) => new(value);
    public ValueTask<Dictionary<int, InteropItem>?> EchoInt32Dictionary(Dictionary<int, InteropItem>? value) => new(value);
    public ValueTask<Dictionary<Guid, long>?> EchoGuidDictionary(Dictionary<Guid, long>? value) => new(value);
    public ValueTask<IDictionary<string, int>?> EchoStringIDictionary(IDictionary<string, int>? value) => new(value);
    public ValueTask<IReadOnlyDictionary<int, string>?> EchoInt32ReadOnlyDictionary(IReadOnlyDictionary<int, string>? value) => new(value);
    public ValueTask<Dictionary<string, List<int[]>>?> EchoNestedContainers(Dictionary<string, List<int[]>>? value) => new(value);

    // ----- Nullable variants -----
    public ValueTask<int?> EchoNullableInt32(int? value) => new(value);
    public ValueTask<long?> EchoNullableInt64(long? value) => new(value);
    public ValueTask<double?> EchoNullableDouble(double? value) => new(value);
    public ValueTask<Guid?> EchoNullableGuid(Guid? value) => new(value);
    public ValueTask<DateTime?> EchoNullableDateTime(DateTime? value) => new(value);
    public ValueTask<InteropEnum?> EchoNullableEnum(InteropEnum? value) => new(value);
    public ValueTask<InteropStruct?> EchoNullableStruct(InteropStruct? value) => new(value);
    public ValueTask<List<int?>?> EchoNullableInt32List(List<int?>? value) => new(value);

    // ----- Enums -----
    public ValueTask<InteropEnum> EchoEnum(InteropEnum value) => new(value);
    public ValueTask<InteropFlags> EchoFlags(InteropFlags value) => new(value);
    public ValueTask<InteropLongEnum> EchoLongEnum(InteropLongEnum value) => new(value);
    public ValueTask<InteropEnum[]?> EchoEnumArray(InteropEnum[]? value) => new(value);

    // ----- Objects, unions, generics, user formatter -----
    public ValueTask<InteropAllTypes?> EchoAllTypes(InteropAllTypes? value) => new(value);
    public ValueTask<InteropStruct> EchoStruct(InteropStruct value) => new(value);
    public ValueTask<InteropRecord?> EchoRecord(InteropRecord? value) => new(value);
    public ValueTask<InteropRecordStruct> EchoRecordStruct(InteropRecordStruct value) => new(value);
    public ValueTask<InteropSparse?> EchoSparse(InteropSparse? value) => new(value);
    public ValueTask<InteropItem?> EchoItem(InteropItem? value) => new(value);
    public ValueTask<InteropNested?> EchoNested(InteropNested? value) => new(value);
    public ValueTask<InteropShape?> EchoShape(InteropShape? value) => new(value);
    public ValueTask<List<InteropShape>?> EchoShapes(List<InteropShape>? value) => new(value);
    public ValueTask<IInteropMessage?> EchoMessage(IInteropMessage? value) => new(value);
    public ValueTask<InteropBox<string>?> EchoStringBox(InteropBox<string>? value) => new(value);
    public ValueTask<InteropBox<InteropItem>?> EchoItemBox(InteropBox<InteropItem>? value) => new(value);
    public ValueTask<InteropPair<int, InteropItem>?> EchoPair(InteropPair<int, InteropItem>? value) => new(value);
    public ValueTask<InteropPoint> EchoPoint(InteropPoint value) => new(value);
    public ValueTask<List<InteropPoint>?> EchoPoints(List<InteropPoint>? value) => new(value);

    // ----- Several parameters, void and non-generic results -----
    public ValueTask<InteropRecord> Combine(string name, int count, InteropStruct point) => new(new InteropRecord(name, count, point));

    public ValueTask Ping() => ValueTask.CompletedTask;

    public void Notify(string message) => _notifications.Enqueue(message);

    public ValueTask<string[]> TakeNotifications()
    {
        var taken = new List<string>();
        while (_notifications.TryDequeue(out var message))
            taken.Add(message);
        return new(taken.ToArray());
    }

    // ----- Large values -----
    public ValueTask<byte[]> CreateBytes(int length, int seed) => new(Pattern(length, seed));

    public ValueTask<string> HashBytes(byte[] data) => new(Hash(data));

    // ----- Cancellation and errors -----
    public async ValueTask<int> Delay(int milliseconds, int value, CancellationToken cancellationToken)
    {
        await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        return value;
    }

    public ValueTask Throw(string message) => throw new InvalidOperationException(message);

    public ValueTask<int> ThrowWithResult(string message) => throw new InvalidOperationException(message);

    // ----- Session -----
    public ValueTask<string> GetIdentity() => new(Context.Identity?.DisplayName ?? "");

    public ValueTask<string> GetEndpoint() => new(Describe(Context.Endpoint));

    // ----- Calls back into the client nexus -----
    public ValueTask<string?> CallClientEchoString(string? value) => Context.Clients.Caller.ClientEchoString(value);

    public ValueTask<InteropAllTypes?> CallClientEchoAllTypes(InteropAllTypes? value) => Context.Clients.Caller.ClientEchoAllTypes(value);

    public ValueTask CallClientNotify(string message)
    {
        Context.Clients.Caller.ClientNotify(message);
        return ValueTask.CompletedTask;
    }

    // ----- Authorization -----
    [NexusAuthorize<InteropPermission>(InteropPermission.Read)]
    public ValueTask<string> ReadSecret() => new(Secret);

    [NexusAuthorize<InteropPermission>(InteropPermission.Admin)]
    public ValueTask<int> AdminEcho(int value) => new(value);

    // ----- Separate invocation pool -----
    public async ValueTask<int> SlowEcho(int value, int milliseconds)
    {
        await Task.Delay(milliseconds).ConfigureAwait(false);
        return value;
    }

    // ----- Pipes and channels -----
    public async ValueTask EchoPipe(INexusDuplexPipe pipe)
    {
        while (true)
        {
            var result = await pipe.Input.ReadAsync().ConfigureAwait(false);
            var buffer = result.Buffer;
            foreach (var segment in buffer)
                await pipe.Output.WriteAsync(segment).ConfigureAwait(false);
            pipe.Input.AdvanceTo(buffer.End);
            if (result.IsCompleted || result.IsCanceled)
                break;
        }

        await pipe.CompleteAsync().ConfigureAwait(false);
    }

    public ValueTask EchoItemChannel(INexusDuplexChannel<InteropItem> channel) => EchoChannel(channel);

    public ValueTask EchoInt64Channel(INexusDuplexChannel<long> channel) => EchoChannel(channel);

    private static async ValueTask EchoChannel<T>(INexusDuplexChannel<T> channel)
    {
        var reader = await channel.GetReaderAsync().ConfigureAwait(false);
        var writer = await channel.GetWriterAsync().ConfigureAwait(false);
        await foreach (var item in reader.ConfigureAwait(false))
            await writer.WriteAsync(item).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);
    }
}
