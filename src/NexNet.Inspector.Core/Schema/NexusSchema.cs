using System.Diagnostics.CodeAnalysis;
using NexNet.Diagnostics;
using NexNet.Serialization;

namespace NexNet.Inspector.Core.Schema;

/// <summary>
/// An inspector schema (<c>.nnschema</c>): the generator's IR of one or more nexus pairs and every serialized type they
/// reach, read from its MessagePack form (docs/internals/nexnet-ir.md §5). It drives <see cref="SchemaDecoder"/>.
/// </summary>
/// <remarks>
/// Immutable once read, so one instance can serve any number of decoders and threads. The schema is always an explicit
/// input: a recording's embedded schema, a workspace override, or none (raw bodies).
/// </remarks>
public sealed class NexusSchema
{
    /// <summary>The IR version this reader understands.</summary>
    public const int SupportedIrVersion = 1;

    private readonly Dictionary<string, SchemaTypeDecl> _types;

    internal NexusSchema(int irVersion, SchemaExport[] exports, SchemaTypeDecl[] types, ulong hash)
    {
        IrVersion = irVersion;
        Exports = exports;
        Types = types;
        Hash = hash;
        _types = new Dictionary<string, SchemaTypeDecl>(types.Length, StringComparer.Ordinal);
        foreach (var type in types)
            _types.TryAdd(type.Id, type);
        Link();
    }

    /// <summary>The IR version the schema was written with.</summary>
    public int IrVersion { get; }

    /// <summary>The exported nexus pairs; an embedded nexus schema has exactly one.</summary>
    public IReadOnlyList<SchemaExport> Exports { get; }

    /// <summary>Every declared type, ascending by ID.</summary>
    public IReadOnlyList<SchemaTypeDecl> Types { get; }

    /// <summary>The schema hash: 64-bit FNV-1a over the bytes it was read from (see <see cref="SchemaHash"/>).</summary>
    public ulong Hash { get; }

    /// <summary>Finds a declared type by ID.</summary>
    /// <param name="id">The type ID (full metadata name of the definition).</param>
    /// <param name="decl">The declaration.</param>
    /// <returns>False when the schema does not contain it.</returns>
    public bool TryGetType(string id, [NotNullWhen(true)] out SchemaTypeDecl? decl) => _types.TryGetValue(id, out decl);

    /// <summary>Reads a schema.</summary>
    /// <param name="bytes">The <c>.nnschema</c> bytes, for example a nexus's <c>InspectorSchema</c>.</param>
    /// <returns>The schema.</returns>
    /// <exception cref="FormatException">The bytes are not a schema this reader understands.</exception>
    public static NexusSchema Read(ReadOnlyMemory<byte> bytes)
        => TryRead(bytes, out var schema, out var error) ? schema : throw new FormatException(error);

    /// <summary>Reads the schema a generated nexus embeds (<c>NexNetEmitInspectorSchema</c>).</summary>
    /// <typeparam name="T">The nexus class.</typeparam>
    /// <returns>The schema.</returns>
    public static NexusSchema Of<T>() where T : IInspectorSchemaSource => Read(T.InspectorSchema.ToArray());

    /// <summary>Reads a schema without throwing.</summary>
    /// <param name="bytes">The <c>.nnschema</c> bytes.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="error">Why the bytes were refused.</param>
    /// <returns>False when the bytes are not a schema this reader understands.</returns>
    public static bool TryRead(ReadOnlyMemory<byte> bytes, [NotNullWhen(true)] out NexusSchema? schema, out string? error)
    {
        try
        {
            schema = SchemaReader.Read(bytes);
            error = null;
            return true;
        }
        catch (NexusSerializationException e)
        {
            schema = null;
            error = "Malformed schema: " + e.Message;
            return false;
        }
        catch (FormatException e)
        {
            schema = null;
            error = e.Message;
            return false;
        }
    }

    // Resolves every declaration reference once, so decoding never looks a type up by name.
    private void Link()
    {
        foreach (var export in Exports)
        {
            LinkInterface(export.ServerApi);
            LinkInterface(export.ClientApi);
        }

        foreach (var type in Types)
        {
            switch (type)
            {
                case SchemaObjectDecl obj:
                    foreach (var member in obj.Members)
                        LinkRef(member.Type);
                    break;
                case SchemaUnionDecl union:
                    foreach (var unionCase in union.Cases)
                        unionCase.Decl = _types.GetValueOrDefault(unionCase.CaseDeclId);
                    break;
            }
        }
    }

    private void LinkInterface(SchemaInterface api)
    {
        foreach (var method in api.Methods)
        {
            LinkRef(method.ReturnType);
            LinkRef(method.PipeType);
            foreach (var parameter in method.Parameters)
                LinkRef(parameter.Type);
        }

        foreach (var collection in api.Collections)
        {
            LinkRef(collection.Item);
            LinkRef(collection.ValuesType);
        }
    }

    private void LinkRef(SchemaTypeRef? type)
    {
        if (type is null)
            return;
        if (type.Kind == SchemaTypeKind.Decl && type.DeclId is { } id)
            type.Decl = _types.GetValueOrDefault(id);
        foreach (var arg in type.Args)
            LinkRef(arg);
    }
}
