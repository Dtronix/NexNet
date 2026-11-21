using NexNet.Generator.Serialization;

namespace NexNet.Generator.Ir;

/// <summary>
/// A native formatter declared with <c>[assembly: NexusTargetFormatter&lt;T&gt;(target, formatter)]</c>, for one target bit.
/// </summary>
/// <param name="ClrName">Full metadata name of the definition of <c>T</c>, as <see cref="TypeRef.Clr"/> holds it on an External reference.</param>
/// <param name="Target">The target bit.</param>
/// <param name="Formatter">Fully qualified name of the native formatter class.</param>
internal sealed record TargetFormatterMapping(string ClrName, int Target, string Formatter);

/// <summary>
/// Attaches the assembly's native formatter mappings to every <see cref="TypeRefKind.External"/> reference of the IR
/// (<see cref="TypeRef.NativeFormatters"/>). Applied to the merged IR, so fragments stay independent of the mappings.
/// </summary>
internal static class IrNativeFormatters
{
    public static NexusIr Apply(NexusIr ir, EquatableArray<TargetFormatterMapping> mappings)
    {
        if (mappings.Length == 0)
            return ir;

        var byClr = mappings
            .GroupBy(m => m.ClrName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new EquatableArray<NativeFormatterIr>(g
                    .OrderBy(m => m.Target)
                    .Select(m => new NativeFormatterIr(m.Target, m.Formatter))
                    .ToArray()),
                StringComparer.Ordinal);

        var rewriter = new Rewriter(byClr);
        return ir with
        {
            Exports = Map(ir.Exports, rewriter.Export),
            Types = Map(ir.Types, rewriter.Decl),
        };
    }

    private static EquatableArray<T> Map<T>(EquatableArray<T> items, Func<T, T> map)
        where T : IEquatable<T>
        => items.Length == 0 ? items : new EquatableArray<T>(items.Select(map).ToArray());

    private sealed class Rewriter
    {
        private readonly Dictionary<string, EquatableArray<NativeFormatterIr>> _byClr;

        public Rewriter(Dictionary<string, EquatableArray<NativeFormatterIr>> byClr) => _byClr = byClr;

        public ExportIr Export(ExportIr export) => export with
        {
            ServerApi = Interface(export.ServerApi),
            ClientApi = Interface(export.ClientApi),
        };

        private InterfaceIr Interface(InterfaceIr api) => api with
        {
            Methods = Map(api.Methods, m => m with
            {
                ReturnType = Ref(m.ReturnType),
                PipeType = Ref(m.PipeType),
                Params = Map(m.Params, p => p with { Type = Ref(p.Type) }),
            }),
            Collections = Map(api.Collections, c => c with { Item = Ref(c.Item) }),
        };

        public TypeDeclIr Decl(TypeDeclIr decl) => decl is ObjectDeclIr obj
            ? obj with { Members = Map(obj.Members, m => m with { Type = Ref(m.Type)! }) }
            : decl;

        private TypeRef? Ref(TypeRef? type)
        {
            if (type == null)
                return null;

            var result = type.Args.Length == 0 ? type : type with { Args = Map(type.Args, a => Ref(a)!) };
            if (type.Kind == TypeRefKind.External && type.Clr != null && _byClr.TryGetValue(type.Clr, out var formatters))
                result = result with { NativeFormatters = formatters };
            return result;
        }
    }
}
