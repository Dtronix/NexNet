using System.Text;
using Microsoft.CodeAnalysis;

namespace NexNet.Generator.Serialization;

/// <summary>
/// Structural hash of a <see cref="TypeShape"/>: a function of exactly what goes on the wire.
/// </summary>
/// <remarks>
/// <para>
/// The hash is FNV-1a over the token stream of a canonical walk: a pre-order depth-first walk from the root in a fixed
/// order (object members by key, union cases by tag, type arguments by position). Every object and union shape gets an
/// index when first reached (the root object is <c>#0</c>); a later reference to it writes its index instead of walking
/// it again, so cycles terminate. The order depends only on keys, tags and positions, so the same structure always
/// produces the same tokens, whatever the declaration order, names or cache state.
/// </para>
/// <para>
/// Hashed: object member keys and types, class vs struct, union tags and case types, enum underlying types and values,
/// <c>Nullable&lt;T&gt;</c>, arrays and their rank, and the .NET identity (metadata name and type arguments) of
/// built-in, CLR and custom-formatter types. Not hashed: names of <c>[NexusObject]</c> types, unions, members and enum
/// members, and reference-type nullability. None of those reach the wire.
/// </para>
/// </remarks>
internal static class ShapeHasher
{
    private enum Token : byte
    {
        Object = 1,
        Union,
        Ref,
        Key,
        Tag,
        Enum,
        Nullable,
        Array,
        Named,
        TypeParam,
    }

    /// <summary>Structural hash of a type: its canonical walk (see the class remarks).</summary>
    public static int Hash(TypeShape root) => new Walker(listing: null).Run(root);

    /// <summary>The hash plus the readable listing of the same walk (tests).</summary>
    public static (int Hash, string Listing) HashWithListing(TypeShape root)
    {
        var listing = new ListingWriter();
        var hash = new Walker(listing).Run(root);
        return (hash, listing.ToString());
    }

    private sealed class Walker
    {
        private IncrementalHasher _hasher = new();
        private readonly Dictionary<TypeShape, int> _index = new(ReferenceEqualityComparer<TypeShape>.Instance);
        private readonly ListingWriter? _listing;

        public Walker(ListingWriter? listing) => _listing = listing;

        public int Run(TypeShape root)
        {
            var rootReference = Visit(root);
            _listing?.Root(rootReference);
            return _hasher.ToHashCode();
        }

        // Writes the shape's tokens and returns its listing reference text.
        private string Visit(TypeShape shape)
        {
            switch (shape)
            {
                case ObjectShape or UnionShape when _index.TryGetValue(shape, out var seen):
                    Add(Token.Ref);
                    _hasher.Add(seen);
                    return "#" + seen;

                case ObjectShape obj:
                {
                    var i = _index.Count;
                    _index.Add(obj, i);
                    var block = _listing?.Begin(i, obj.IsValueType ? "struct" : "object", obj.Type.Name);
                    Add(Token.Object);
                    _hasher.Add((byte)(obj.IsValueType ? 1 : 0));
                    _hasher.Add(obj.Members.Count);
                    foreach (var m in obj.Members)
                    {
                        Add(Token.Key);
                        _hasher.Add(m.Key);
                        var reference = Visit(m.Type);
                        block?.Add(m.Key + ": " + reference);
                    }

                    return "#" + i;
                }

                case UnionShape union:
                {
                    var i = _index.Count;
                    _index.Add(union, i);
                    var block = _listing?.Begin(i, "union", union.Type.Name);
                    Add(Token.Union);
                    _hasher.Add(union.Cases.Count);
                    foreach (var (tag, c) in union.Cases.OrderBy(c => c.Tag))
                    {
                        Add(Token.Tag);
                        _hasher.Add(tag);
                        var reference = Visit(c);
                        block?.Add("tag " + tag + ": " + reference);
                    }

                    return "#" + i;
                }

                case EnumShape e:
                    Add(Token.Enum);
                    _hasher.Add((int)e.Underlying);
                    _hasher.Add(e.Values.Length);
                    foreach (var v in e.Values)
                        _hasher.Add(v);
                    return "enum " + SpecialName(e) + " {" + string.Join(", ", e.Values) + "}";

                case NullableShape n:
                    Add(Token.Nullable);
                    return Visit(n.Inner) + "?";

                case ArrayShape a:
                    Add(Token.Array);
                    _hasher.Add(a.Rank);
                    return Visit(a.Element) + "[" + new string(',', a.Rank - 1) + "]";

                case NamedShape named:
                {
                    Add(Token.Named);
                    _hasher.Add((byte)named.Kind);
                    _hasher.AddString(named.MetadataName);
                    _hasher.Add(named.Arguments.Length);
                    var args = named.Arguments.Select(Visit).ToArray();
                    return named.Type.Name + (args.Length == 0 ? "" : "<" + string.Join(", ", args) + ">");
                }

                case TypeParameterShape p:
                    Add(Token.TypeParam);
                    _hasher.Add(p.Ordinal);
                    return "!" + p.Ordinal;
            }

            throw new InvalidOperationException("Unknown shape " + shape.GetType().Name);
        }

        private void Add(Token token) => _hasher.Add((byte)token);

        private static string SpecialName(EnumShape e)
            => ((INamedTypeSymbol)e.Type).EnumUnderlyingType?.Name ?? e.Underlying.ToString();
    }

    /// <summary>
    /// Renders the walk as a shape listing: a root line, then one block per indexed object or union in index order.
    /// Type names in block headers are labels for readers; they are not hashed.
    /// </summary>
    private sealed class ListingWriter
    {
        private readonly List<Block> _blocks = new();
        private string _root = "";

        public Block Begin(int index, string kind, string label)
        {
            var block = new Block("#" + index + " " + kind + " " + label);
            _blocks.Add(block); // indices are assigned in Begin order
            return block;
        }

        public void Root(string reference) => _root = reference;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("root: ").Append(_root);
            foreach (var block in _blocks)
            {
                sb.Append('\n').Append(block.Header);
                foreach (var line in block.Lines)
                    sb.Append("\n  ").Append(line);
            }

            return sb.ToString();
        }

        public sealed class Block
        {
            public Block(string header) => Header = header;

            public string Header { get; }

            public List<string> Lines { get; } = new();

            public void Add(string line) => Lines.Add(line);
        }
    }
}
