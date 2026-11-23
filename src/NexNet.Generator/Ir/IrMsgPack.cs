using System.Text;

namespace NexNet.Generator.Ir;

/// <summary>
/// The MessagePack form of the IR (the inspector schema, <c>.nnschema</c>): a deterministic mirror of
/// <see cref="IrJson"/>. Every record is a positional array in a fixed field order (no maps, so no key order to keep
/// stable), enums are their numeric values, null is nil, integers and headers use their smallest encoding with
/// non-negative integers in the unsigned family. That is the canonical form NexNet's <c>MsgPackWriter</c> produces,
/// so a reader can re-encode a decoded schema and get the same bytes. The layout is documented in
/// docs/internals/nexnet-ir.md §5; source locations are in-memory only and not written.
/// </summary>
/// <remarks>
/// The generator is a netstandard2.0 analyzer and cannot reference NexNet's runtime writer, hence this small emitter.
/// </remarks>
internal static class IrMsgPack
{
    /// <summary>First element of a declaration array: an <see cref="ObjectDeclIr"/>.</summary>
    public const int DeclObject = 0;

    /// <summary>First element of a declaration array: a <see cref="UnionDeclIr"/>.</summary>
    public const int DeclUnion = 1;

    /// <summary>First element of a declaration array: an <see cref="EnumDeclIr"/>.</summary>
    public const int DeclEnum = 2;

    /// <summary>Writes <paramref name="ir"/> as MessagePack.</summary>
    public static byte[] Write(NexusIr ir)
    {
        var w = new Emitter();
        w.Array(3);
        w.Int(ir.IrVersion);
        w.Array(ir.Exports.Length);
        foreach (var export in ir.Exports)
            WriteExport(w, export);
        w.Array(ir.Types.Length);
        foreach (var decl in ir.Types)
            WriteDecl(w, decl);
        return w.ToArray();
    }

    /// <summary>
    /// The schema hash: 64-bit FNV-1a over the MessagePack bytes. Stable across builds because the bytes are; readers
    /// can recompute it to check a schema they received.
    /// </summary>
    public static ulong Hash(byte[] bytes)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    private static void WriteExport(Emitter w, ExportIr e)
    {
        w.Array(8);
        w.String(e.Name);
        w.String(e.ClrNamespace);
        w.Int(e.Targets);
        WriteInterface(w, e.ServerApi);
        WriteInterface(w, e.ClientApi);
        w.Array(3);
        w.String(e.Handshake.Version);
        w.Int(e.Handshake.ServerNexusHash);
        w.Int(e.Handshake.ClientNexusHash);
        w.Array(e.ClientAcceptedIds.Length);
        foreach (var accepted in e.ClientAcceptedIds)
        {
            w.Array(2);
            w.Int(accepted.Hash);
            w.Int(accepted.Id);
        }

        w.String(e.Docs);
    }

    private static void WriteInterface(Emitter w, InterfaceIr i)
    {
        w.Array(7);
        w.String(i.ClrName);
        w.String(i.Name);
        w.String(i.ClrNamespace);
        w.Int(i.Hash);
        w.Array(i.Methods.Length);
        foreach (var method in i.Methods)
            WriteMethod(w, method);
        w.Array(i.Collections.Length);
        foreach (var collection in i.Collections)
            WriteCollection(w, collection);
        w.Array(i.Versions.Length);
        foreach (var version in i.Versions)
        {
            w.Array(3);
            w.String(version.Version);
            w.Int(version.Hash);
            w.Array(version.MethodIds.Length);
            foreach (var id in version.MethodIds)
                w.Int(id);
        }
    }

    private static void WriteMethod(Emitter w, MethodIr m)
    {
        w.Array(13);
        w.String(m.Name);
        w.Int(m.Id);
        w.Int(m.Hash);
        w.Int((byte)m.ReturnKind);
        WriteTypeRef(w, m.ReturnType);
        w.Array(m.Params.Length);
        foreach (var p in m.Params)
        {
            w.Array(5);
            w.String(p.Name);
            w.Int((byte)p.Kind);
            WriteTypeRef(w, p.Type);
            w.Int(p.WireIndex);
            w.Bool(p.Nullable);
        }

        w.Int(m.SerializedCount);
        w.Bool(m.HasCancellation);
        w.Int((byte)m.PipeKind);
        WriteTypeRef(w, m.PipeType);
        w.Bool(m.SeparatePool);
        w.Bool(m.IsAuthorized);
        w.String(m.Docs);
    }

    private static void WriteCollection(Emitter w, CollectionIr c)
    {
        w.Array(8);
        w.String(c.Name);
        w.Int(c.Id);
        w.Int(c.Hash);
        w.Int((byte)c.Kind);
        w.Int((byte)c.Mode);
        WriteTypeRef(w, c.Item);
        w.Bool(c.IsAuthorized);
        w.String(c.Docs);
    }

    private static void WriteTypeRef(Emitter w, TypeRef? t)
    {
        if (t == null)
        {
            w.Nil();
            return;
        }

        w.Array(9);
        w.Int((byte)t.Kind);
        w.Int((byte)t.Prim);
        w.String(t.Clr);
        w.Array(t.Args.Length);
        foreach (var arg in t.Args)
            WriteTypeRef(w, arg);
        w.String(t.DeclId);
        w.Int(t.Ordinal);
        w.Bool(t.RefNullable);
        w.Bool(t.JsNumber);
        w.Array(t.NativeFormatters.Length);
        foreach (var formatter in t.NativeFormatters)
        {
            w.Array(2);
            w.Int(formatter.Target);
            w.String(formatter.Formatter);
        }
    }

    private static void WriteDecl(Emitter w, TypeDeclIr d)
    {
        switch (d)
        {
            case ObjectDeclIr obj:
                WriteDeclHeader(w, 15, DeclObject, d);
                w.Bool(obj.IsStruct);
                w.Bool(obj.IsRecord);
                w.Bool(obj.IsAbstract);
                WriteStrings(w, obj.TypeParams);
                w.String(obj.BaseDeclId);
                w.Int(obj.ArrayLength);
                w.Bool(obj.IsRecursive);
                w.Array(obj.Members.Length);
                foreach (var m in obj.Members)
                {
                    w.Array(7);
                    w.String(m.Name);
                    w.Int(m.Key);
                    WriteTypeRef(w, m.Type);
                    w.Bool(m.RefNullable);
                    w.Bool(m.WasNonPublic);
                    if (m.DefaultLiteral == null)
                    {
                        w.Nil();
                    }
                    else
                    {
                        w.Array(2);
                        w.Int((byte)m.DefaultLiteral.Kind);
                        w.String(m.DefaultLiteral.Value);
                    }

                    w.String(m.Docs);
                }

                return;

            case UnionDeclIr union:
                WriteDeclHeader(w, 11, DeclUnion, d);
                w.Bool(union.IsInterface);
                WriteStrings(w, union.TypeParams);
                w.Bool(union.IsRecursive);
                w.Array(union.Cases.Length);
                foreach (var c in union.Cases)
                {
                    w.Array(3);
                    w.Int(c.Tag);
                    w.String(c.CaseDeclId);
                    w.Int(c.InheritanceDepth);
                }

                return;

            case EnumDeclIr e:
                WriteDeclHeader(w, 10, DeclEnum, d);
                w.Int((byte)e.Underlying);
                w.Bool(e.IsFlags);
                w.Array(e.Members.Length);
                foreach (var m in e.Members)
                {
                    w.Array(2);
                    w.String(m.Name);
                    w.Int(m.Value);
                }

                return;
        }

        throw new InvalidOperationException("Unknown declaration " + d.GetType().Name);
    }

    private static void WriteDeclHeader(Emitter w, int length, int kind, TypeDeclIr d)
    {
        w.Array(length);
        w.Int(kind);
        w.String(d.Id);
        w.String(d.ClrMetadataName);
        w.String(d.ClrNamespace);
        w.String(d.Name);
        WriteStrings(w, d.ContainingTypes);
        w.String(d.Docs);
    }

    private static void WriteStrings(Emitter w, Serialization.EquatableArray<string> values)
    {
        w.Array(values.Length);
        foreach (var value in values)
            w.String(value);
    }

    /// <summary>The MessagePack primitives the IR needs, each in its smallest (canonical) encoding.</summary>
    private sealed class Emitter
    {
        private byte[] _buffer = new byte[1024];
        private int _length;

        public byte[] ToArray()
        {
            var result = new byte[_length];
            System.Array.Copy(_buffer, result, _length);
            return result;
        }

        public void Nil() => Byte(0xc0);

        public void Bool(bool value) => Byte(value ? (byte)0xc3 : (byte)0xc2);

        public void Int(long value)
        {
            if (value >= 0)
            {
                // Non-negative values use the unsigned family, as canonical writers (and NexNet's) do.
                if (value <= 0x7f)
                    Byte((byte)value);
                else if (value <= byte.MaxValue)
                    Header(0xcc, (ulong)value, 1);
                else if (value <= ushort.MaxValue)
                    Header(0xcd, (ulong)value, 2);
                else if (value <= uint.MaxValue)
                    Header(0xce, (ulong)value, 4);
                else
                    Header(0xcf, (ulong)value, 8);
            }
            else if (value >= -32)
                Byte(unchecked((byte)value));
            else if (value >= sbyte.MinValue)
                Header(0xd0, unchecked((ulong)value), 1);
            else if (value >= short.MinValue)
                Header(0xd1, unchecked((ulong)value), 2);
            else if (value >= int.MinValue)
                Header(0xd2, unchecked((ulong)value), 4);
            else
                Header(0xd3, unchecked((ulong)value), 8);
        }

        public void Array(int count)
        {
            if (count <= 15)
                Byte((byte)(0x90 | count));
            else if (count <= ushort.MaxValue)
                Header(0xdc, (ulong)count, 2);
            else
                Header(0xdd, (ulong)count, 4);
        }

        public void String(string? value)
        {
            if (value == null)
            {
                Nil();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length <= 31)
                Byte((byte)(0xa0 | bytes.Length));
            else if (bytes.Length <= byte.MaxValue)
                Header(0xd9, (ulong)bytes.Length, 1);
            else if (bytes.Length <= ushort.MaxValue)
                Header(0xda, (ulong)bytes.Length, 2);
            else
                Header(0xdb, (ulong)bytes.Length, 4);
            Ensure(bytes.Length);
            System.Array.Copy(bytes, 0, _buffer, _length, bytes.Length);
            _length += bytes.Length;
        }

        // A code byte followed by the low 'size' bytes of 'value', big-endian.
        private void Header(byte code, ulong value, int size)
        {
            Ensure(1 + size);
            _buffer[_length++] = code;
            for (var shift = (size - 1) * 8; shift >= 0; shift -= 8)
                _buffer[_length++] = unchecked((byte)(value >> shift));
        }

        private void Byte(byte value)
        {
            Ensure(1);
            _buffer[_length++] = value;
        }

        private void Ensure(int count)
        {
            if (_length + count <= _buffer.Length)
                return;
            var grown = new byte[Math.Max(_buffer.Length * 2, _length + count)];
            System.Array.Copy(_buffer, grown, _length);
            _buffer = grown;
        }
    }
}
