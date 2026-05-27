using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NexNet.Testing.Recording;

/// <summary>
/// Mapping from <see cref="MethodInfo"/> to the <c>ushort</c> method id assigned by the source
/// generator. Mirrors the generator's <c>AssignMethodIds</c> algorithm at runtime: directly-
/// declared methods first (in metadata-token order, which matches source declaration order),
/// then inherited-interface methods grouped per-interface and ordered by interface full name
/// (matching the generator's <c>OrderBy(i =&gt; i.ToDisplayString(), StringComparer.Ordinal)</c>).
/// Explicit <see cref="NexusMethodAttribute.MethodId"/> values take precedence; the remaining
/// methods receive sequential ids that skip the reserved set.
/// </summary>
/// <remarks>
/// AOT/trimming caveat: metadata-token order remains stable under standard runtimes today; if a
/// future host re-orders <c>GetMethods()</c> output, assertions will surface a clear "method not
/// recorded" failure rather than silently match the wrong method. See
/// <c>MethodIdMapTests.GeneratorParity_EditorServerInterface_AssignsExpectedIds</c> (and the
/// matching client-interface test) for the regression tests that lock in the expected layout.
/// </remarks>
internal static class MethodIdMap
{
    public static Dictionary<MethodInfo, ushort> Build(Type interfaceType)
    {
        var methods = CollectDeclaredMethods(interfaceType);

        var explicitIds = new HashSet<ushort>();
        foreach (var m in methods)
        {
            var attr = m.GetCustomAttribute<NexusMethodAttribute>();
            if (attr is { MethodId: > 0 }) explicitIds.Add(attr.MethodId);
        }

        var result = new Dictionary<MethodInfo, ushort>();
        ushort next = 0;
        foreach (var m in methods)
        {
            var attr = m.GetCustomAttribute<NexusMethodAttribute>();
            ushort id;
            if (attr is { MethodId: > 0 })
            {
                id = attr.MethodId;
            }
            else
            {
                while (explicitIds.Contains(next)) next++;
                id = next++;
            }
            result[m] = id;
        }
        return result;
    }

    /// <summary>
    /// Enumerates methods in the same order the generator's <c>AssignMethodIds</c> uses:
    /// directly-declared methods first, then each inherited interface's directly-declared
    /// methods, with inherited interfaces ordered by full type name (ordinal).
    /// </summary>
    private static IReadOnlyList<MethodInfo> CollectDeclaredMethods(Type interfaceType)
    {
        const BindingFlags directOnly =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var ordered = new List<MethodInfo>();
        ordered.AddRange(interfaceType.GetMethods(directOnly));

        var inherited = interfaceType.GetInterfaces()
            .OrderBy(i => i.FullName ?? i.Name, StringComparer.Ordinal);
        foreach (var iface in inherited)
            ordered.AddRange(iface.GetMethods(directOnly));

        return ordered;
    }
}
