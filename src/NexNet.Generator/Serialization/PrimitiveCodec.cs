namespace NexNet.Generator.Serialization;

/// <summary>
/// Types written and read directly with <c>MsgPackWriter</c>/<c>MsgPackReader</c> methods (no formatter object).
/// Keyed by the fully qualified type name as produced by <see cref="SymbolUtilities.FullSymbolType"/>.
/// </summary>
internal static class PrimitiveCodec
{
    public static bool TryGetReadMethod(string? fullTypeName, out string readMethod)
    {
        readMethod = null!;
        if (fullTypeName == null)
            return false;

        switch (fullTypeName)
        {
            case "global::System.Boolean": readMethod = "ReadBoolean"; return true;
            case "global::System.Byte": readMethod = "ReadByte"; return true;
            case "global::System.SByte": readMethod = "ReadSByte"; return true;
            case "global::System.Int16": readMethod = "ReadInt16"; return true;
            case "global::System.UInt16": readMethod = "ReadUInt16"; return true;
            case "global::System.Int32": readMethod = "ReadInt32"; return true;
            case "global::System.UInt32": readMethod = "ReadUInt32"; return true;
            case "global::System.Int64": readMethod = "ReadInt64"; return true;
            case "global::System.UInt64": readMethod = "ReadUInt64"; return true;
            case "global::System.Single": readMethod = "ReadSingle"; return true;
            case "global::System.Double": readMethod = "ReadDouble"; return true;
            case "global::System.Char": readMethod = "ReadChar"; return true;
            case "global::System.String":
            case "global::System.String?":
                readMethod = "ReadString";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Emits a statement writing <paramref name="valueExpression"/> of the given type.
    /// </summary>
    public static string WriteStatement(string fullTypeName, string valueExpression, string writer)
    {
        if (TryGetReadMethod(fullTypeName, out _))
            return $"{writer}.Write({valueExpression});";

        return $"global::NexNet.Serialization.NexusFormatterRegistry.Get<{fullTypeName}>().Serialize(ref {writer}, {valueExpression});";
    }

    /// <summary>
    /// Emits a statement reading into the already declared variable <paramref name="target"/>.
    /// </summary>
    public static string ReadStatement(string fullTypeName, string target, string reader)
    {
        if (TryGetReadMethod(fullTypeName, out var method))
            return $"{target} = {reader}.{method}();";

        return $"global::NexNet.Serialization.NexusFormatterRegistry.Get<{fullTypeName}>().Deserialize(ref {reader}, ref {target});";
    }
}
