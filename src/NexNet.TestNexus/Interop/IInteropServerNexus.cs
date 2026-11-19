using System.Buffers;
using System.Numerics;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.TestNexus.Interop;

/// <summary>
/// Server side of the interop nexus: echo methods for the whole type zoo (protocol specification §10) plus the method
/// kinds the cross-language tests exercise. Every Echo method returns its argument unchanged.
/// </summary>
public partial interface IInteropServerNexus
{
    // ----- Built-in types (§10.4) -----
    ValueTask<bool> EchoBool(bool value);
    ValueTask<sbyte> EchoSByte(sbyte value);
    ValueTask<byte> EchoByte(byte value);
    ValueTask<short> EchoInt16(short value);
    ValueTask<ushort> EchoUInt16(ushort value);
    ValueTask<int> EchoInt32(int value);
    ValueTask<uint> EchoUInt32(uint value);
    ValueTask<long> EchoInt64(long value);
    ValueTask<ulong> EchoUInt64(ulong value);
    ValueTask<float> EchoSingle(float value);
    ValueTask<double> EchoDouble(double value);
    ValueTask<Half> EchoHalf(Half value);
    ValueTask<char> EchoChar(char value);
    ValueTask<decimal> EchoDecimal(decimal value);
    ValueTask<string?> EchoString(string? value);
    ValueTask<byte[]?> EchoBytes(byte[]? value);
    ValueTask<Memory<byte>> EchoMemory(Memory<byte> value);
    ValueTask<ReadOnlyMemory<byte>> EchoReadOnlyMemory(ReadOnlyMemory<byte> value);
    ValueTask<ReadOnlySequence<byte>> EchoReadOnlySequence(ReadOnlySequence<byte> value);
    ValueTask<ArraySegment<byte>> EchoArraySegment(ArraySegment<byte> value);
    ValueTask<DateTime> EchoDateTime(DateTime value);
    ValueTask<DateTimeOffset> EchoDateTimeOffset(DateTimeOffset value);
    ValueTask<TimeSpan> EchoTimeSpan(TimeSpan value);
    ValueTask<DateOnly> EchoDateOnly(DateOnly value);
    ValueTask<TimeOnly> EchoTimeOnly(TimeOnly value);
    ValueTask<Guid> EchoGuid(Guid value);
    ValueTask<BigInteger> EchoBigInteger(BigInteger value);
    ValueTask<Uri?> EchoUri(Uri? value);
    ValueTask<Version?> EchoVersion(Version? value);
    ValueTask<(int, string?, double)> EchoValueTuple((int, string?, double) value);
    ValueTask<(int, long, string, bool, double, Guid, byte)> EchoValueTuple7((int, long, string, bool, double, Guid, byte) value);
    ValueTask<Tuple<int, string>?> EchoTuple(Tuple<int, string>? value);
    ValueTask<KeyValuePair<string, int>> EchoKeyValuePair(KeyValuePair<string, int> value);

    // ----- Ext 78 primitive arrays (§10.5) -----
    ValueTask<sbyte[]?> EchoSByteArray(sbyte[]? value);
    ValueTask<short[]?> EchoInt16Array(short[]? value);
    ValueTask<ushort[]?> EchoUInt16Array(ushort[]? value);
    ValueTask<int[]?> EchoInt32Array(int[]? value);
    ValueTask<uint[]?> EchoUInt32Array(uint[]? value);
    ValueTask<long[]?> EchoInt64Array(long[]? value);
    ValueTask<ulong[]?> EchoUInt64Array(ulong[]? value);
    ValueTask<float[]?> EchoSingleArray(float[]? value);
    ValueTask<double[]?> EchoDoubleArray(double[]? value);
    ValueTask<char[]?> EchoCharArray(char[]? value);
    ValueTask<Half[]?> EchoHalfArray(Half[]? value);
    ValueTask<List<int>?> EchoInt32List(List<int>? value);
    ValueTask<Memory<long>> EchoInt64Memory(Memory<long> value);
    ValueTask<ReadOnlyMemory<double>> EchoDoubleReadOnlyMemory(ReadOnlyMemory<double> value);

    // ----- Containers -----
    ValueTask<string[]?> EchoStringArray(string[]? value);
    ValueTask<List<string>?> EchoStringList(List<string>? value);
    ValueTask<IList<string>?> EchoStringIList(IList<string>? value);
    ValueTask<IReadOnlyList<InteropItem>?> EchoItemReadOnlyList(IReadOnlyList<InteropItem>? value);
    ValueTask<HashSet<string>?> EchoStringSet(HashSet<string>? value);
    ValueTask<HashSet<int>?> EchoInt32Set(HashSet<int>? value);
    ValueTask<Queue<int>?> EchoInt32Queue(Queue<int>? value);
    ValueTask<Stack<string>?> EchoStringStack(Stack<string>? value);
    ValueTask<Dictionary<string, string>?> EchoStringDictionary(Dictionary<string, string>? value);
    ValueTask<Dictionary<int, InteropItem>?> EchoInt32Dictionary(Dictionary<int, InteropItem>? value);
    ValueTask<Dictionary<Guid, long>?> EchoGuidDictionary(Dictionary<Guid, long>? value);
    ValueTask<IDictionary<string, int>?> EchoStringIDictionary(IDictionary<string, int>? value);
    ValueTask<IReadOnlyDictionary<int, string>?> EchoInt32ReadOnlyDictionary(IReadOnlyDictionary<int, string>? value);
    ValueTask<Dictionary<string, List<int[]>>?> EchoNestedContainers(Dictionary<string, List<int[]>>? value);

    // ----- Nullable variants -----
    ValueTask<int?> EchoNullableInt32(int? value);
    ValueTask<long?> EchoNullableInt64(long? value);
    ValueTask<double?> EchoNullableDouble(double? value);
    ValueTask<Guid?> EchoNullableGuid(Guid? value);
    ValueTask<DateTime?> EchoNullableDateTime(DateTime? value);
    ValueTask<InteropEnum?> EchoNullableEnum(InteropEnum? value);
    ValueTask<InteropStruct?> EchoNullableStruct(InteropStruct? value);
    ValueTask<List<int?>?> EchoNullableInt32List(List<int?>? value);

    // ----- Enums -----
    ValueTask<InteropEnum> EchoEnum(InteropEnum value);
    ValueTask<InteropFlags> EchoFlags(InteropFlags value);
    ValueTask<InteropLongEnum> EchoLongEnum(InteropLongEnum value);
    ValueTask<InteropEnum[]?> EchoEnumArray(InteropEnum[]? value);

    // ----- Objects, unions, generics, user formatter -----
    ValueTask<InteropAllTypes?> EchoAllTypes(InteropAllTypes? value);
    ValueTask<InteropStruct> EchoStruct(InteropStruct value);
    ValueTask<InteropRecord?> EchoRecord(InteropRecord? value);
    ValueTask<InteropRecordStruct> EchoRecordStruct(InteropRecordStruct value);
    ValueTask<InteropSparse?> EchoSparse(InteropSparse? value);
    ValueTask<InteropItem?> EchoItem(InteropItem? value);
    ValueTask<InteropNested?> EchoNested(InteropNested? value);
    ValueTask<InteropShape?> EchoShape(InteropShape? value);
    ValueTask<List<InteropShape>?> EchoShapes(List<InteropShape>? value);
    ValueTask<IInteropMessage?> EchoMessage(IInteropMessage? value);
    ValueTask<InteropBox<string>?> EchoStringBox(InteropBox<string>? value);
    ValueTask<InteropBox<InteropItem>?> EchoItemBox(InteropBox<InteropItem>? value);
    ValueTask<InteropPair<int, InteropItem>?> EchoPair(InteropPair<int, InteropItem>? value);
    ValueTask<InteropPoint> EchoPoint(InteropPoint value);
    ValueTask<List<InteropPoint>?> EchoPoints(List<InteropPoint>? value);

    // ----- Several parameters, void and non-generic results -----
    ValueTask<InteropRecord> Combine(string name, int count, InteropStruct point);
    ValueTask Ping();
    void Notify(string message);
    ValueTask<string[]> TakeNotifications();

    // ----- Large values (above one 64 KiB frame) -----
    /// <summary>Returns <paramref name="length"/> bytes of <see cref="InteropServerNexus.Pattern"/>.</summary>
    ValueTask<byte[]> CreateBytes(int length, int seed);
    /// <summary>Lower-case hex SHA-256 of <paramref name="data"/>.</summary>
    ValueTask<string> HashBytes(byte[] data);

    // ----- Cancellation and errors -----
    /// <summary>Waits <paramref name="milliseconds"/>, then returns <paramref name="value"/>.</summary>
    ValueTask<int> Delay(int milliseconds, int value, CancellationToken cancellationToken);
    /// <summary>Throws <see cref="InvalidOperationException"/> with <paramref name="message"/>.</summary>
    ValueTask Throw(string message);
    /// <summary>Throws <see cref="InvalidOperationException"/> with <paramref name="message"/>.</summary>
    ValueTask<int> ThrowWithResult(string message);

    // ----- Session -----
    /// <summary>The display name of the session's identity; empty when it has none.</summary>
    ValueTask<string> GetIdentity();
    /// <summary>The session's endpoint as <c>name|transport|isSecure|path</c>; empty when it has none.</summary>
    ValueTask<string> GetEndpoint();

    // ----- Calls back into the client nexus -----
    ValueTask<string?> CallClientEchoString(string? value);
    ValueTask<InteropAllTypes?> CallClientEchoAllTypes(InteropAllTypes? value);
    ValueTask CallClientNotify(string message);

    // ----- Authorization (see InteropServerNexus.OnAuthorize) -----
    /// <summary>Requires <see cref="InteropPermission.Read"/>.</summary>
    ValueTask<string> ReadSecret();
    /// <summary>Requires <see cref="InteropPermission.Admin"/>.</summary>
    ValueTask<int> AdminEcho(int value);

    // ----- Separate invocation pool -----
    /// <summary>Waits <paramref name="milliseconds"/> in the separate pool, then returns <paramref name="value"/>.</summary>
    [NexusMethod(SeparatePool = true)]
    ValueTask<int> SlowEcho(int value, int milliseconds);

    // ----- Pipes and channels -----
    /// <summary>Writes back every byte it reads until the client completes, then completes.</summary>
    ValueTask EchoPipe(INexusDuplexPipe pipe);
    /// <summary>Writes back every item it reads until the client completes, then completes.</summary>
    ValueTask EchoItemChannel(INexusDuplexChannel<InteropItem> channel);
    /// <summary>Writes back every value it reads until the client completes, then completes.</summary>
    ValueTask EchoInt64Channel(INexusDuplexChannel<long> channel);

    // ----- Collection -----
    [NexusCollection(NexusCollectionMode.BiDirectional)]
    INexusList<InteropItem> Items { get; }
}
