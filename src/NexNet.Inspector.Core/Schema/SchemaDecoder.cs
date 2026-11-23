using NexNet.Inspector.Core.Assembly;

namespace NexNet.Inspector.Core.Schema;

/// <summary>
/// Decodes message bodies with a schema: invocation arguments and results, channel items and collection messages,
/// into <see cref="DecodedValue"/> trees. No reflection: the walk is driven by the schema's type descriptions alone.
/// </summary>
/// <remarks>
/// <para>
/// It never throws on bad input. A method the schema does not know, or no schema at all, gives one
/// <see cref="FieldKind.Raw"/> field over the bytes; a value of the wrong shape is skipped whole; bytes that end early
/// end in a raw field to the end.
/// </para>
/// <para>Thread-safe: each call has its own state. To decode an assembler's rows, use <see cref="SessionDecoder"/>.</para>
/// </remarks>
public sealed class SchemaDecoder
{
    /// <summary>Creates a decoder.</summary>
    /// <param name="schema">The schema, or null to keep every body raw (framing is still decoded upstream).</param>
    /// <param name="options">Limits; <see cref="SchemaDecoderOptions.Default"/> when null.</param>
    public SchemaDecoder(NexusSchema? schema, SchemaDecoderOptions? options = null)
    {
        Schema = schema;
        Options = options ?? SchemaDecoderOptions.Default;
    }

    /// <summary>The schema; null when bodies are decoded raw.</summary>
    public NexusSchema? Schema { get; }

    /// <summary>The limits.</summary>
    public SchemaDecoderOptions Options { get; }

    /// <summary>Decodes one value of a known type (a channel item, a collection value, a result).</summary>
    /// <param name="type">The type; null decodes the value as <see cref="FieldKind.Unknown"/>.</param>
    /// <param name="bytes">Exactly the value's bytes.</param>
    /// <param name="range">Where the bytes came from, carried to <see cref="DecodedValue.Range"/>.</param>
    /// <param name="name">Name of the root field.</param>
    public DecodedValue DecodeValue(SchemaTypeRef? type, ReadOnlyMemory<byte> bytes, PayloadRange range = default, string? name = null)
    {
        var writer = new FieldWriter(bytes, Options);
        writer.Value(type, name);
        writer.Finish();
        return new DecodedValue(range, bytes, writer.Fields);
    }

    /// <summary>Decodes the arguments array of an invocation.</summary>
    /// <param name="method">The method, or null when the schema does not know the method ID (the bytes stay raw).</param>
    /// <param name="bytes">The arguments array (<see cref="InvocationRow.Arguments"/>).</param>
    /// <param name="range">Where the bytes came from.</param>
    public DecodedValue DecodeArguments(SchemaMethod? method, ReadOnlyMemory<byte> bytes, PayloadRange range = default)
    {
        if (method is null)
            return DecodeRaw(bytes, range, Schema is null ? DecodeProblem.NoSchema : DecodeProblem.UnknownMethod);

        var writer = new FieldWriter(bytes, Options);
        writer.Arguments(method);
        writer.Finish();
        return new DecodedValue(range, bytes, writer.Fields);
    }

    /// <summary>Decodes the arguments of a collection's connect invocation: the pipe ID.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="bytes">The arguments array.</param>
    /// <param name="range">Where the bytes came from.</param>
    public DecodedValue DecodeCollectionArguments(SchemaCollection collection, ReadOnlyMemory<byte> bytes, PayloadRange range = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var writer = new FieldWriter(bytes, Options);
        writer.CollectionArguments(collection);
        writer.Finish();
        return new DecodedValue(range, bytes, writer.Fields);
    }

    /// <summary>Decodes the embedded value of an invocation result.</summary>
    /// <param name="method">The invoked method, or null when unknown.</param>
    /// <param name="state">The result's state: a completed result is the return value, an exception its detail.</param>
    /// <param name="bytes">The embedded value (<see cref="InvocationRow.Value"/>).</param>
    /// <param name="range">Where the bytes came from.</param>
    public DecodedValue DecodeResult(SchemaMethod? method, InvocationResultState state, ReadOnlyMemory<byte> bytes, PayloadRange range = default)
    {
        if (Schema is null)
            return DecodeRaw(bytes, range, DecodeProblem.NoSchema);

        var writer = new FieldWriter(bytes, Options);
        if (state == InvocationResultState.Exception)
            writer.ExceptionDetail();
        else if (method is null)
            writer.Raw(DecodeProblem.UnknownMethod);
        else
            writer.Value(method.ReturnKind == SchemaReturnKind.TaskOf ? method.ReturnType : null, "Result");
        writer.Finish();
        return new DecodedValue(range, bytes, writer.Fields);
    }

    /// <summary>Decodes one collection message (spec §4.11).</summary>
    /// <param name="collection">The collection, or null when unknown (values are then skipped as unknown).</param>
    /// <param name="bytes">The channel item's value.</param>
    /// <param name="range">Where the bytes came from.</param>
    public DecodedValue DecodeCollectionMessage(SchemaCollection? collection, ReadOnlyMemory<byte> bytes, PayloadRange range = default)
    {
        var writer = new FieldWriter(bytes, Options);
        writer.CollectionMessage(collection?.Item, collection?.ValuesType);
        writer.Finish();
        return new DecodedValue(range, bytes, writer.Fields);
    }

    /// <summary>Wraps bytes in one raw field.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="range">Where the bytes came from.</param>
    /// <param name="problem">Why they stay raw.</param>
    public static DecodedValue DecodeRaw(ReadOnlyMemory<byte> bytes, PayloadRange range, DecodeProblem problem)
    {
        var writer = new FieldWriter(bytes, SchemaDecoderOptions.Default);
        writer.Raw(problem);
        return new DecodedValue(range, bytes, writer.Fields);
    }
}
