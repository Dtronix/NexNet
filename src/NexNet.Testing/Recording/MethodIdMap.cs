using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace NexNet.Testing.Recording;

/// <summary>
/// Mapping from <see cref="MethodInfo"/> to the <c>ushort</c> method id assigned by the source
/// generator. Mirrors the generator's id-assignment at runtime so the assertion API resolves the
/// same id the generator burned into the proxy/nexus types.
/// </summary>
/// <remarks>
/// <para>
/// Parity rules — these MUST stay in lockstep with <c>NexusDataExtractor.ExtractInterfaceData</c> /
/// <c>AssignMethodIds</c> in the generator:
/// </para>
/// <list type="bullet">
/// <item>Directly-declared methods first (metadata-token order, which matches source declaration
/// order), then each inherited interface's directly-declared methods.</item>
/// <item>Inherited interfaces ordered by a C#-style display name (ordinal), matching the
/// generator's <c>AllInterfaces.OrderBy(i =&gt; i.ToDisplayString(), StringComparer.Ordinal)</c>.
/// <c>Type.FullName</c> is deliberately NOT used: it diverges from <c>ToDisplayString()</c> for
/// generic interfaces (CLR <c>`1[[System.Guid, ...]]</c> vs C# <c>&lt;System.Guid&gt;</c>) and for
/// nested interfaces (<c>+</c> vs <c>.</c>), which would shift ids on those shapes.</item>
/// <item>Methods marked <c>[NexusMethod(Ignore = true)]</c> are excluded entirely — the generator
/// filters them out before assigning ids, so they receive no id and never reserve a slot.</item>
/// <item>Explicit <see cref="NexusMethodAttribute.MethodId"/> values take precedence; the remaining
/// methods receive sequential ids that skip the explicitly-reserved set.</item>
/// </list>
/// <para>
/// AOT/trimming caveat: metadata-token order remains stable under standard runtimes today; if a
/// future host re-orders <c>GetMethods()</c> output, assertions surface a clear "method not
/// recorded" failure rather than silently matching the wrong method. See <c>MethodIdMapTests</c>
/// for the regression tests that lock in the demo-interface layout, ignored-method filtering, and
/// generic-inherited-interface ordering.
/// </para>
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
    /// directly-declared methods first, then each inherited interface's directly-declared methods,
    /// with inherited interfaces ordered by C#-style display name (ordinal). Methods marked
    /// <c>[NexusMethod(Ignore = true)]</c> are excluded, matching the generator's pre-assignment
    /// filter.
    /// </summary>
    private static IReadOnlyList<MethodInfo> CollectDeclaredMethods(Type interfaceType)
    {
        const BindingFlags directOnly =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var ordered = new List<MethodInfo>();
        ordered.AddRange(interfaceType.GetMethods(directOnly).Where(NotIgnored));

        var inherited = interfaceType.GetInterfaces()
            .OrderBy(GeneratorOrderingKey, StringComparer.Ordinal);
        foreach (var iface in inherited)
            ordered.AddRange(iface.GetMethods(directOnly).Where(NotIgnored));

        return ordered;
    }

    private static bool NotIgnored(MethodInfo method)
        => method.GetCustomAttribute<NexusMethodAttribute>() is not { Ignore: true };

    /// <summary>
    /// Builds a C#-style display name for <paramref name="type"/> whose ordinal ordering matches
    /// Roslyn's <c>INamedTypeSymbol.ToDisplayString()</c> — the generator's inherited-interface
    /// sort key. Handles namespaces, nested types ('.'-joined), generic arguments (rendered in
    /// <c>&lt;&gt;</c>, each recursively and namespace-qualified), arrays, <see cref="Nullable{T}"/>,
    /// and the C# keyword spellings of the built-in special types.
    /// </summary>
    /// <remarks>
    /// Generic args declared on an enclosing type (the rare <c>Outer&lt;T&gt;.IInner</c> nesting)
    /// are rendered at the innermost level rather than distributed across the nesting chain; this
    /// is a benign deviation for a code shape that does not occur on NexNet RPC interfaces.
    /// </remarks>
    private static string GeneratorOrderingKey(Type type)
    {
        var sb = new StringBuilder();
        AppendDisplayName(sb, type);
        return sb.ToString();
    }

    private static void AppendDisplayName(StringBuilder sb, Type type)
    {
        if (CSharpKeyword(type) is { } keyword)
        {
            sb.Append(keyword);
            return;
        }

        if (type.IsArray)
        {
            AppendDisplayName(sb, type.GetElementType()!);
            sb.Append('[').Append(',', type.GetArrayRank() - 1).Append(']');
            return;
        }

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            AppendDisplayName(sb, underlying);
            sb.Append('?');
            return;
        }

        if (type.IsNested && type.DeclaringType is { } declaring)
        {
            AppendDisplayName(sb, declaring);
            sb.Append('.');
        }
        else if (!string.IsNullOrEmpty(type.Namespace))
        {
            sb.Append(type.Namespace).Append('.');
        }

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name.Substring(0, tick);
        sb.Append(name);

        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            sb.Append('<');
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendDisplayName(sb, args[i]);
            }
            sb.Append('>');
        }
    }

    /// <summary>
    /// Returns the C# keyword spelling for a built-in special type (matching Roslyn's
    /// <c>UseSpecialTypes</c> display option), or <c>null</c> for everything else. Compares by exact
    /// type to avoid the <see cref="Type.GetTypeCode"/> pitfall where an enum reports its underlying
    /// primitive's type code.
    /// </summary>
    private static string? CSharpKeyword(Type type)
    {
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(sbyte)) return "sbyte";
        if (type == typeof(char)) return "char";
        if (type == typeof(short)) return "short";
        if (type == typeof(ushort)) return "ushort";
        if (type == typeof(int)) return "int";
        if (type == typeof(uint)) return "uint";
        if (type == typeof(long)) return "long";
        if (type == typeof(ulong)) return "ulong";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(string)) return "string";
        if (type == typeof(object)) return "object";
        if (type == typeof(void)) return "void";
        return null;
    }
}
