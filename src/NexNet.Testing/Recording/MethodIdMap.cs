using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NexNet.Testing.Recording;

/// <summary>
/// Best-effort mapping from <see cref="MethodInfo"/> to the <c>ushort</c> method id assigned
/// by the source generator. Mirrors the generator's <c>AssignMethodIds</c> algorithm at
/// runtime: explicit <see cref="NexusMethodAttribute.MethodId"/> values take precedence, the
/// remaining methods get sequential ids skipping the reserved set.
/// </summary>
/// <remarks>
/// The runtime sees methods in <c>Type.GetMethods()</c> order; for a single interface
/// declaration this is metadata-token order, which matches C# source order and the
/// generator's behavior. If the generator's ordering ever diverges, assertions that depend on
/// this map will surface a clear "method not recorded" failure rather than silently match the
/// wrong method.
/// </remarks>
internal static class MethodIdMap
{
    public static Dictionary<MethodInfo, ushort> Build(System.Type interfaceType)
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

    private static IReadOnlyList<MethodInfo> CollectDeclaredMethods(System.Type interfaceType)
    {
        // GetMethods on an interface returns the interface's own methods (no Object methods).
        // For inherited interfaces, append their methods in interface declaration order.
        var direct = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var inherited = interfaceType.GetInterfaces()
            .SelectMany(i => i.GetMethods(BindingFlags.Public | BindingFlags.Instance));
        return direct.Concat(inherited).Distinct().ToList();
    }
}
