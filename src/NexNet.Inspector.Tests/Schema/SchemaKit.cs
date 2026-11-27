using NexNet.Diagnostics;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Tests.Assembly;

namespace NexNet.Inspector.Tests.Schema;

/// <summary>Schema and decoded-tree helpers shared by the schema tests.</summary>
internal static class SchemaKit
{
    private static readonly Lazy<NexusSchema> LazyServer = new(NexusSchema.Of<DecodeServerNexus>);

    /// <summary>The generated schema of <see cref="DecodeServerNexus"/>.</summary>
    public static NexusSchema Server => LazyServer.Value;

    public static byte[] Bytes<T>() where T : IInspectorSchemaSource => T.InspectorSchema.ToArray();

    public static ulong Hash<T>() where T : IInspectorSchemaSource => T.InspectorSchemaHash;

    public static SchemaMethod Method(string name) => Server.Exports[0].ServerApi.Methods.Single(m => m.Name == name);

    public static SchemaTypeRef Type(string id)
    {
        var type = new SchemaTypeRef(SchemaTypeKind.Decl, declId: id);
        type.Decl = Server.Types.Single(t => t.Id == id);
        return type;
    }

    /// <summary>The children of a field, in order.</summary>
    public static List<DecodedField> Children(DecodedValue value, in DecodedField field)
    {
        var result = new List<DecodedField>();
        for (var c = field.FirstChild; c >= 0; c = value.Fields[c].NextSibling)
            result.Add(value.Fields[c]);
        return result;
    }

    /// <summary>The child with a name.</summary>
    public static DecodedField Child(DecodedValue value, in DecodedField field, string name)
        => Children(value, field).Single(c => c.Name == name);

    /// <summary>A path of child names from the root.</summary>
    public static DecodedField At(DecodedValue value, params string[] path)
    {
        var field = value.Fields[0];
        foreach (var name in path)
            field = Child(value, field, name);
        return field;
    }

    /// <summary>
    /// Checks the tree's structure: every child lies inside its parent, children are in order without overlap, the
    /// top-level fields cover the bytes exactly, and a container's bytes are its header plus its children.
    /// </summary>
    public static string? CheckCoverage(DecodedValue value)
    {
        var fields = value.Fields;
        var position = 0;
        for (var top = fields.Length > 0 ? 0 : -1; top >= 0; top = fields[top].NextSibling)
        {
            if (fields[top].Offset != position)
                return $"top-level field {top} starts at {fields[top].Offset}, expected {position}";
            position = fields[top].End;
        }

        if (position != value.Bytes.Length)
            return $"top-level fields end at {position}, bytes end at {value.Bytes.Length}";

        foreach (var field in fields)
        {
            if (field.FirstChild < 0)
                continue;

            // Containers: header, then the children back to back (a map entry has no header of its own).
            var at = field.Offset + field.HeaderLength;
            var count = 0;
            for (var c = field.FirstChild; c >= 0; c = fields[c].NextSibling)
            {
                if (fields[c].Parent != field.Index)
                    return $"field {c} has parent {fields[c].Parent}, expected {field.Index}";
                if (fields[c].Offset != at)
                    return $"field {c} starts at {fields[c].Offset}, expected {at}";
                at = fields[c].End;
                count++;
            }

            if (count != field.ChildCount)
                return $"field {field.Index} counts {field.ChildCount} children, has {count}";
            if (at != field.End)
                return $"children of field {field.Index} end at {at}, field ends at {field.End}";
        }

        return null;
    }
}

/// <summary>Replays a session into an assembler with a <see cref="SessionDecoder"/> attached before the first record.</summary>
internal sealed class SessionReplay
{
    private readonly InMemoryStreamBytes _inbound = new();
    private readonly InMemoryStreamBytes _outbound = new();
    private readonly FrameParser _inboundParser;
    private readonly FrameParser _outboundParser;
    private long _op;

    public SessionReplay(NexusSchema? schema, NntlSessionRole? role, bool inboundPreamble = true, bool outboundPreamble = true)
    {
        Assembler = new MessageAssembler(_inbound, _outbound, role);
        Decoder = new SessionDecoder(Assembler, schema);
        _inboundParser = new FrameParser(TapDirection.Inbound, inboundPreamble);
        _outboundParser = new FrameParser(TapDirection.Outbound, outboundPreamble);
    }

    public MessageAssembler Assembler { get; }

    public SessionDecoder Decoder { get; }

    public void Push(TapDirection direction, byte[] bytes)
    {
        var store = direction == TapDirection.Inbound ? _inbound : _outbound;
        var parser = direction == TapDirection.Inbound ? _inboundParser : _outboundParser;
        store.Append(bytes);
        parser.Push(_op, 1000 + _op, bytes, Assembler);
        _op++;
    }

    public SessionReplay Complete()
    {
        _outboundParser.Complete(Assembler);
        _inboundParser.Complete(Assembler);
        Assembler.Complete();
        return this;
    }

    /// <summary>Replays a scripted session (no preamble unless the script has one).</summary>
    public static SessionReplay Run(WireScript script, NexusSchema? schema, NntlSessionRole? role = null)
    {
        var replay = new SessionReplay(schema, role, inboundPreamble: false, outboundPreamble: script.HasPreamble);
        foreach (var (direction, bytes) in script.Frames)
            replay.Push(direction, bytes);
        return replay.Complete();
    }
}
