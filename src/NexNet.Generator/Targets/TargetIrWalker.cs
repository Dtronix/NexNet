using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets;

/// <summary>Which declared types of the IR belong to which export and target.</summary>
internal static class TargetIrWalker
{
    /// <summary>The ids of the declared types an export reaches through its interfaces, ordinal order.</summary>
    public static IReadOnlyList<string> ReachableDecls(NexusIr ir, ExportIr export)
    {
        var decls = ir.Types.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<TypeRef>();

        foreach (var api in new[] { export.ServerApi, export.ClientApi })
        {
            foreach (var method in api.Methods)
            {
                Push(pending, method.ReturnType);
                Push(pending, method.PipeType);
                foreach (var param in method.Params)
                    Push(pending, param.Type);
            }

            foreach (var collection in api.Collections)
                Push(pending, collection.Item);
        }

        var pendingDecls = new Stack<string>();
        while (pending.Count > 0 || pendingDecls.Count > 0)
        {
            if (pending.Count > 0)
            {
                var type = pending.Pop();
                if (type.Kind == TypeRefKind.Decl && type.DeclId != null && found.Add(type.DeclId))
                    pendingDecls.Push(type.DeclId);
                foreach (var arg in type.Args)
                    pending.Push(arg);
                continue;
            }

            if (!decls.TryGetValue(pendingDecls.Pop(), out var decl))
                continue;

            switch (decl)
            {
                case ObjectDeclIr obj:
                    foreach (var member in obj.Members)
                        pending.Push(member.Type);
                    if (obj.BaseDeclId != null && found.Add(obj.BaseDeclId))
                        pendingDecls.Push(obj.BaseDeclId);
                    break;
                case UnionDeclIr union:
                    foreach (var unionCase in union.Cases)
                    {
                        if (found.Add(unionCase.CaseDeclId))
                            pendingDecls.Push(unionCase.CaseDeclId);
                    }
                    break;
            }
        }

        return found.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The declared types emitted for a target, each with the export that owns it: the first export (IR order) opted in
    /// to the target that reaches it. Types no export reaches ([NexusObject] and <c>NexusSerializable</c> roots) are
    /// emitted for every target in use and owned by the first export opted in to the target. Empty when no export
    /// opts in to the target. Ordered by type id.
    /// </summary>
    public static IReadOnlyList<(TypeDeclIr Decl, ExportIr Owner)> TargetDecls(NexusIr ir, NexusTargets flag)
    {
        var exports = ir.Exports.Where(e => (e.Targets & (int)flag) != 0).ToArray();
        if (exports.Length == 0)
            return Array.Empty<(TypeDeclIr, ExportIr)>();

        var owners = new Dictionary<string, ExportIr>(StringComparer.Ordinal);
        foreach (var export in exports)
        {
            foreach (var id in ReachableDecls(ir, export))
            {
                if (!owners.ContainsKey(id))
                    owners[id] = export;
            }
        }

        // Types reached by an export without this target are not emitted for it, unless a target export reaches them.
        var reachedByOthers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in ir.Exports.Where(e => (e.Targets & (int)flag) == 0))
        {
            foreach (var id in ReachableDecls(ir, export))
                reachedByOthers.Add(id);
        }

        var result = new List<(TypeDeclIr, ExportIr)>();
        foreach (var decl in ir.Types)
        {
            if (owners.TryGetValue(decl.Id, out var owner))
                result.Add((decl, owner));
            else if (!reachedByOthers.Contains(decl.Id))
                result.Add((decl, exports[0]));
        }

        return result;
    }

    /// <summary>The declared types of <see cref="TargetDecls"/> owned by the export.</summary>
    public static IEnumerable<TypeDeclIr> OwnedDecls(NexusIr ir, ExportIr export, NexusTargets flag)
        => TargetDecls(ir, flag).Where(d => ReferenceEquals(d.Owner, export) || d.Owner.Equals(export)).Select(d => d.Decl);

    private static void Push(Stack<TypeRef> pending, TypeRef? type)
    {
        if (type != null)
            pending.Push(type);
    }
}
