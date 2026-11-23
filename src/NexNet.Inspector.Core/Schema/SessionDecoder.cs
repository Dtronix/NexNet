using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.Core.Schema;

/// <summary>
/// Decodes the rows of one session's <see cref="MessageAssembler"/> with a schema, and tells the assembler what the
/// schema knows about pipes: which argument of a pipe method carries the pipe ID (exact opener linking) and whether the
/// pipe is raw, a typed channel or a collection.
/// </summary>
/// <remarks>
/// Create it before the first record is added, so every pipe is resolved as it opens. With a null schema it attaches
/// nothing: the assembler keeps its heuristic opener linking and every body decodes raw. Not thread-safe (the
/// assembler is not).
/// </remarks>
public sealed class SessionDecoder : IPipeContentResolver
{
    private readonly MessageAssembler _assembler;
    private SchemaExport? _export;
    private bool _exportFixed;

    /// <summary>Creates a decoder for a session and attaches it to the assembler as its pipe content resolver.</summary>
    /// <param name="assembler">The session's assembler.</param>
    /// <param name="schema">The schema chosen for the session (embedded, workspace override), or null for raw only.</param>
    /// <param name="options">Decoding limits.</param>
    public SessionDecoder(MessageAssembler assembler, NexusSchema? schema, SchemaDecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        _assembler = assembler;
        Decoder = new SchemaDecoder(schema, options);
        if (schema is not null)
            assembler.PipeContentResolver = this;
    }

    /// <summary>The value decoder.</summary>
    public SchemaDecoder Decoder { get; }

    /// <summary>The schema; null when bodies stay raw.</summary>
    public NexusSchema? Schema => Decoder.Schema;

    /// <summary>
    /// The nexus pair the session speaks. By default the schema's only export, or the one whose hashes match the
    /// session's greetings; set it to choose one explicitly.
    /// </summary>
    public SchemaExport? Export
    {
        get
        {
            if (!_exportFixed)
                _export = SelectExport(out _exportFixed);
            return _export;
        }
        set
        {
            _export = value;
            _exportFixed = true;
        }
    }

    /// <summary>
    /// True when the client made this invocation (it targets the server API); null when the session's role is not
    /// known yet.
    /// </summary>
    /// <param name="invocation">The invocation.</param>
    public bool? IsClientCall(in InvocationRow invocation)
    {
        if (_assembler.Role is not { } role)
            return null;
        // The tapped endpoint sends Outbound: it is the caller when the request went out.
        return (invocation.Direction == TapDirection.Outbound) == (role == NntlSessionRole.Client);
    }

    /// <summary>Finds the method an invocation calls.</summary>
    /// <param name="invocation">The invocation; its request must have been parsed.</param>
    /// <param name="method">The method.</param>
    /// <returns>False without a schema, before the request is parsed, or for an ID that is not a method (see <see cref="TryGetCollection"/>).</returns>
    public bool TryGetMethod(in InvocationRow invocation, [NotNullWhen(true)] out SchemaMethod? method)
    {
        method = null;
        if (!invocation.RequestParsed || Export is not { } export)
            return false;

        switch (IsClientCall(invocation))
        {
            case true:
                return export.ServerApi.TryGetMethod(invocation.MethodId, out method);
            case false:
                return export.ClientApi.TryGetMethod(invocation.MethodId, out method);
        }

        // Unknown role: only an ID that exists on one side alone is certain.
        var onServer = export.ServerApi.TryGetMethod(invocation.MethodId, out var serverMethod);
        var onClient = export.ClientApi.TryGetMethod(invocation.MethodId, out var clientMethod);
        method = onServer && !onClient ? serverMethod : onClient && !onServer ? clientMethod : null;
        return method is not null;
    }

    /// <summary>Finds the collection a connect invocation (or a collection pipe) belongs to.</summary>
    /// <param name="collectionId">The collection ID.</param>
    /// <param name="collection">The collection.</param>
    /// <returns>False without a schema or for an unknown ID.</returns>
    public bool TryGetCollection(ushort collectionId, [NotNullWhen(true)] out SchemaCollection? collection)
    {
        // Collections live on the server nexus; only clients connect to them.
        collection = null;
        return Export is { } export && export.ServerApi.TryGetCollection(collectionId, out collection);
    }

    /// <summary>Decodes an invocation's arguments.</summary>
    /// <param name="invocation">The invocation.</param>
    /// <returns>The decoded value; without fields when the request or its bytes were not captured.</returns>
    public DecodedValue DecodeArguments(in InvocationRow invocation)
    {
        if (!TryGetBytes(invocation.Arguments, out var bytes, out var range))
            return Empty(invocation.Arguments);
        if (Schema is null)
            return SchemaDecoder.DecodeRaw(bytes, range, DecodeProblem.NoSchema);
        if (TryGetMethod(invocation, out var method))
            return Decoder.DecodeArguments(method, bytes, range);
        if (IsClientCall(invocation) != false && TryGetCollection(invocation.MethodId, out var collection))
            return Decoder.DecodeCollectionArguments(collection, bytes, range);
        return SchemaDecoder.DecodeRaw(bytes, range, DecodeProblem.UnknownMethod);
    }

    /// <summary>Decodes an invocation's result value, or its exception detail.</summary>
    /// <param name="invocation">The invocation.</param>
    /// <returns>The decoded value; without fields when the result carries no value or its bytes are missing.</returns>
    public DecodedValue DecodeResult(in InvocationRow invocation)
    {
        if (!TryGetBytes(invocation.Value, out var bytes, out var range))
            return Empty(invocation.Value);
        TryGetMethod(invocation, out var method);
        return Decoder.DecodeResult(method, invocation.ResultState, bytes, range);
    }

    /// <summary>Decodes a channel item: a typed channel's value, or a collection pipe's collection message.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The decoded value; without fields while the item's length prefix is incomplete.</returns>
    public DecodedValue DecodeChannelItem(in ChannelItemRow item)
    {
        if (!TryGetBytes(item.Value, out var bytes, out var range))
            return Empty(item.Value);
        if (Schema is null)
            return SchemaDecoder.DecodeRaw(bytes, range, DecodeProblem.NoSchema);

        var pipe = _assembler.Pipes[item.Pipe];
        if (pipe.Content == PipeContent.Collection)
        {
            SchemaCollection? collection = null;
            if (pipe.Collection >= 0)
                TryGetCollection(_assembler.Collections[pipe.Collection].CollectionId, out collection);
            return Decoder.DecodeCollectionMessage(collection, bytes, range);
        }

        var itemType = ChannelItemType(pipe);
        return itemType is null
            ? SchemaDecoder.DecodeRaw(bytes, range, DecodeProblem.UnknownType)
            : Decoder.DecodeValue(itemType, bytes, range, "Item");
    }

    /// <summary>Decodes the channel item that carried a collection message.</summary>
    /// <param name="message">The collection message.</param>
    public DecodedValue DecodeCollectionMessage(in CollectionMessageRow message)
        => DecodeChannelItem(_assembler.ChannelItems[message.Item]);

    /// <inheritdoc />
    public PipeContent Resolve(in InvocationRow opener)
    {
        if (TryGetMethod(opener, out var method))
            return method.PipeKind == SchemaPipeKind.Channel ? PipeContent.Channel : PipeContent.Raw;
        if (IsClientCall(opener) != false && TryGetCollection(opener.MethodId, out _))
            return PipeContent.Collection;
        return PipeContent.Raw;
    }

    /// <inheritdoc />
    public bool TryGetPipeArgument(in InvocationRow invocation, out int argument)
    {
        if (TryGetMethod(invocation, out var method))
        {
            argument = method.PipeArgumentIndex;
            return true;
        }

        if (IsClientCall(invocation) != false && TryGetCollection(invocation.MethodId, out _))
        {
            argument = 0;
            return true;
        }

        argument = -1;
        return false;
    }

    /// <summary>
    /// A short name for what a pipe carries, for one-line labels: a collection pipe's collection ("Users"), a channel's
    /// item type ("LogLine"). Null for a raw pipe, or when the schema does not say.
    /// </summary>
    /// <param name="pipe">Index into the assembler's pipes.</param>
    public string? GetPipeLabel(int pipe)
    {
        if ((uint)pipe >= (uint)_assembler.Pipes.Length || Schema is null)
            return null;

        var row = _assembler.Pipes[pipe];
        switch (row.Content)
        {
            case PipeContent.Collection when row.Collection >= 0:
                return TryGetCollection(_assembler.Collections[row.Collection].CollectionId, out var collection) ? collection.Name : null;
            case PipeContent.Channel:
                return ChannelItemType(row)?.Decl?.Name;
            default:
                return null;
        }
    }

    // A channel's item type comes from its opener's channel parameter.
    private SchemaTypeRef? ChannelItemType(in PipeRow pipe)
    {
        if (pipe.OpenerInvocation < 0 || !TryGetMethod(_assembler.Invocations[pipe.OpenerInvocation], out var method))
            return null;
        if (method.PipeArgumentIndex >= 0 && method.GetWireParam(method.PipeArgumentIndex) is { Type: { } type })
            return type;
        return method.PipeType;
    }

    private SchemaExport? SelectExport(out bool certain)
    {
        certain = false;
        var exports = Schema?.Exports;
        if (exports is null || exports.Count == 0)
        {
            certain = Schema is not null;
            return null;
        }

        if (exports.Count == 1)
        {
            certain = true;
            return exports[0];
        }

        foreach (var greeting in _assembler.Greetings)
        {
            foreach (var export in exports)
            {
                var matches = greeting.Kind == GreetingKind.Server
                    ? greeting.ClientNexusHash == export.ClientApi.Hash
                    : greeting.ClientNexusHash == export.ClientApi.Hash && export.ServerApi.MatchesHash(greeting.ServerNexusHash);
                if (matches)
                {
                    certain = greeting.Kind != GreetingKind.Server;
                    return export;
                }
            }
        }

        return exports[0];
    }

    // Reads a row's range; a message that ended early (truncated capture) gives the bytes that did arrive.
    private bool TryGetBytes(PayloadRange range, out ReadOnlyMemory<byte> bytes, out PayloadRange actual)
    {
        bytes = default;
        actual = range;
        if (range.IsNone)
            return false;

        if (!_assembler.TryGetPayload(range, out var sequence))
        {
            var available = AvailableLength(range.Message) - range.Offset;
            if (available <= 0 || available >= range.Length)
                return false;
            actual = range with { Length = available };
            if (!_assembler.TryGetPayload(actual, out sequence))
                return false;
        }

        bytes = sequence.IsSingleSegment ? sequence.First : sequence.ToArray();
        return true;
    }

    private long AvailableLength(in MessageRef message)
    {
        switch (message.Kind)
        {
            case MessageRefKind.Assembled when (uint)message.Index < (uint)_assembler.Messages.Length:
                return _assembler.Messages[message.Index].Length;
            case MessageRefKind.ChannelItem when (uint)message.Index < (uint)_assembler.ChannelItems.Length:
                return _assembler.ChannelItems[message.Index].Length;
            case MessageRefKind.Frame when _assembler.TryGetFrame(message.Direction, message.Seq, out var frame):
                return frame.BodyLength;
            default:
                return 0;
        }
    }

    private static DecodedValue Empty(PayloadRange range)
        => new(range, ReadOnlyMemory<byte>.Empty, []);
}
