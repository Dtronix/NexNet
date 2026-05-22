using System;
using System.Collections.Generic;
using System.Reflection;
using MemoryPack;

namespace NexNet.Testing.Recording;

/// <summary>
/// Deserializes the raw argument bytes from an <see cref="InvocationRecord"/> back into the
/// per-parameter values using the same MemoryPack-over-ValueTuple shape the source generator
/// uses at the call site.
/// </summary>
internal static class ArgumentDeserializer
{
    /// <summary>
    /// Builds the open generic <c>ValueTuple&lt;...&gt;</c> type that the generator's call-site
    /// serializer uses for the given method's serializable parameters.
    /// </summary>
    public static Type? GetTupleType(MethodInfo method)
    {
        var serializable = GetSerializableParameterTypes(method);
        if (serializable.Length == 0) return null;
        return MakeValueTupleType(serializable);
    }

    /// <summary>
    /// Deserializes <paramref name="argsBytes"/> into a per-parameter object array, indexed
    /// positionally over the serializable parameters of <paramref name="method"/>. Returns an
    /// empty array when the method has no serializable parameters.
    /// </summary>
    public static object?[] Deserialize(MethodInfo method, ReadOnlyMemory<byte> argsBytes)
    {
        var tupleType = GetTupleType(method);
        if (tupleType is null) return Array.Empty<object?>();

        // MemoryPackSerializer.Deserialize<T>(ReadOnlySpan<byte>) is the public API; for an
        // unknown closed generic we go through the non-generic Type overload.
        var deserialized = MemoryPackSerializer.Deserialize(tupleType, argsBytes.Span);
        if (deserialized is null) return Array.Empty<object?>();

        var fields = tupleType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        var result = new object?[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            result[i] = fields[i].GetValue(deserialized);
        return result;
    }

    private static Type[] GetSerializableParameterTypes(MethodInfo method)
    {
        var list = new List<Type>();
        foreach (var p in method.GetParameters())
        {
            if (IsSerializableParameter(p.ParameterType))
                list.Add(p.ParameterType);
        }
        return list.ToArray();
    }

    private static bool IsSerializableParameter(Type t)
    {
        // Mirrors the generator's exclusion list: CancellationToken, pipe/channel handles.
        if (t.FullName == "System.Threading.CancellationToken") return false;
        var ns = t.Namespace;
        if (ns is not null && (ns.StartsWith("NexNet.Pipes") || ns == "NexNet.Pipes"))
            return false;
        return true;
    }

    private static Type MakeValueTupleType(Type[] elementTypes)
    {
        return elementTypes.Length switch
        {
            1 => typeof(ValueTuple<>).MakeGenericType(elementTypes),
            2 => typeof(ValueTuple<,>).MakeGenericType(elementTypes),
            3 => typeof(ValueTuple<,,>).MakeGenericType(elementTypes),
            4 => typeof(ValueTuple<,,,>).MakeGenericType(elementTypes),
            5 => typeof(ValueTuple<,,,,>).MakeGenericType(elementTypes),
            6 => typeof(ValueTuple<,,,,,>).MakeGenericType(elementTypes),
            7 => typeof(ValueTuple<,,,,,,>).MakeGenericType(elementTypes),
            _ => throw new NotSupportedException(
                $"Method with {elementTypes.Length} serializable parameters is not supported by the test harness yet.")
        };
    }
}
