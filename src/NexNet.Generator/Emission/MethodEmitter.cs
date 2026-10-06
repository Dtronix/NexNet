using System.Text;
using NexNet.Generator.Models;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Emission;

/// <summary>
/// Emits method invocation code.
/// Works entirely with cached data records - no semantic model access.
/// </summary>
internal static class MethodEmitter
{
    /// <summary>
    /// Emit the code for the nexus method invocation (receiving calls).
    /// </summary>
    public static void EmitNexusInvocation(
        StringBuilder sb,
        MethodData method,
        InvocationInterfaceData proxyImplementation)
    {
        const string argPrefix = "__arg";

        // Emit auth guard before any deserialization
        if (method.AuthorizeData != null)
        {
            sb.AppendLine($$"""
                        if (!await this.CheckAuthorization(
                            {{method.Id}},
                            "{{method.Name}}",
                            __authPerms_{{method.Id}},
                            message.InvocationId,
                            returnBuffer != null,
                            {{method.AuthorizeData!.CacheDurationSeconds}}).ConfigureAwait(false))
                            return;
""");
        }

        // Create the cancellation token parameter.
        if (method.CancellationTokenParameter != null)
        {
            sb.AppendLine("                        cts = methodInvoker.RegisterCancellationToken(message.InvocationId);");
        }

        // Deserialize the arguments.
        if (method.SerializedParameterCount > 0)
        {
            // Arguments are a MessagePack array written inline by the proxy (no ValueTuple). They are read by a
            // generated static helper because ref struct readers cannot be locals of async methods before C# 13.
            sb.Append("                        __ReadArguments_").Append(method.Id).Append("(message.Arguments, methodInvoker.SerializerOptions");
            foreach (var p in method.Parameters)
            {
                if (p.SerializedType == null)
                    continue;
                sb.Append(", out var __arg").Append(p.SerializedId);
            }

            sb.AppendLine(");");
        }

        // Register the duplex pipe if we have one.
        if (method.UtilizesPipes)
        {
            sb.Append("                        duplexPipe = await methodInvoker.RegisterDuplexPipe(").Append(argPrefix);
            sb.Append(method.DuplexPipeParameter!.SerializedId);
            sb.AppendLine(").ConfigureAwait(false);");
        }

        sb.Append("                        this.Context.Logger?.NexusLog(\"");
        EmitNexusMethodInvocation(sb, method, true, argPrefix);
        sb.AppendLine("\");");
        sb.Append("                        ");

        // Ignore the return value if we are a void method or a duplex pipe method
        if (method.IsReturnVoid)
        {
            EmitNexusMethodInvocation(sb, method, false, argPrefix);
        }
        else if (method.IsAsync)
        {
            // If we are async, we need to await the method invocation and then serialize the return value otherwise
            // we can just invoke the method and serialize the return value
            if (method.IsAsync && method.ReturnType == null)
            {
                sb.Append("await ");
                EmitNexusMethodInvocation(sb, method, false, argPrefix);
            }
            else
            {
                sb.Append("var result = await ");
                EmitNexusMethodInvocation(sb, method, false, argPrefix);
                sb.AppendLine("                        if (returnBuffer != null)");
                sb.Append("                            global::NexNet.Serialization.NexusSerializer.Serialize<").Append(method.ReturnType).AppendLine(">(returnBuffer, result);");
            }
        }
    }

    /// <summary>
    /// Emits the static helper that reads the MessagePack argument array of a method.
    /// </summary>
    public static void EmitArgumentReader(StringBuilder sb, MethodData method)
    {
        if (method.SerializedParameterCount == 0)
            return;

        // Formatters read into `ref T?`; a peer may send nil for a non-nullable reference argument, as before.
        sb.AppendLine("#pragma warning disable CS8601");
        sb.Append("        private static void __ReadArguments_").Append(method.Id)
            .Append("(global::System.Memory<byte> __arguments, global::NexNet.Serialization.NexusSerializerOptions __options");
        foreach (var p in method.Parameters)
        {
            if (p.SerializedType == null)
                continue;
            sb.Append(", out ").Append(p.SerializedType).Append(" __arg").Append(p.SerializedId);
        }

        sb.AppendLine(")");
        sb.AppendLine("        {");
        sb.AppendLine("            var __reader = new global::NexNet.Serialization.MsgPackReader(__arguments, __options);");
        sb.Append("            if (__reader.ReadArrayHeader() != ").Append(method.SerializedParameterCount)
            .Append(") throw global::NexNet.Serialization.NexusSerializationException.ArgumentCountMismatch(")
            .Append(method.Id).Append(", ").Append(method.SerializedParameterCount).AppendLine(");");
        foreach (var p in method.Parameters)
        {
            if (p.SerializedType == null)
                continue;

            var local = "__arg" + p.SerializedId;
            sb.Append("            ").Append(local).AppendLine(" = default!;");
            sb.Append("            ").AppendLine(PrimitiveCodec.ReadStatement(p.SerializedType, local, "__reader"));
        }

        sb.AppendLine("        }");
        sb.AppendLine("#pragma warning restore CS8601");
        sb.AppendLine();
    }

    /// <summary>
    /// Emits the invocation of the method on the nexus.
    /// </summary>
    /// <param name="sb">StringBuilder to append to.</param>
    /// <param name="method">Method data.</param>
    /// <param name="forLog">Change the output to write the output params. Used for logging.</param>
    private static void EmitNexusMethodInvocation(StringBuilder sb, MethodData method, bool forLog, string argPrefix)
    {
        sb.Append(method.Name).Append("(");

        bool addedParam = false;
        foreach (var param in method.Parameters)
        {
            // If we have a duplex pipe, we need to pass it in the correct parameter position,
            // otherwise we need to pass the serialized value.
            if (param.IsDuplexPipe)
            {
                if (forLog)
                {
                    sb.Append(param.Name)
                        .Append(" = {").Append(argPrefix)
                        .Append(method.DuplexPipeParameter!.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append("duplexPipe, ");
                }

                addedParam = true;
            }
            else if (param.IsDuplexChannel)
            {
                if (forLog)
                {
                    sb.Append(param.Name)
                        .Append(" = {").Append(argPrefix)
                        .Append(method.DuplexPipeParameter!.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append("global::NexNet.Pipes.NexusDuplexPipeExtensions.GetChannel<");
                    sb.Append(param.ChannelType);
                    sb.Append(">(duplexPipe), ");
                }

                addedParam = true;
            }
            else if (param.SerializedValue != null)
            {
                if (forLog)
                {
                    sb.Append(param.Name)
                        .Append(" = {").Append(argPrefix)
                        .Append(param.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append(argPrefix).Append(param.SerializedId).Append(", ");
                }

                addedParam = true;
            }
        }

        if (method.CancellationTokenParameter != null)
        {
            if (forLog)
            {
                sb.Append(method.CancellationTokenParameter.Name).Append(" = ct");
            }
            else
            {
                sb.Append("cts.Token");
            }
        }
        else
        {
            if (addedParam)
                sb.Remove(sb.Length - 2, 2);
        }

        // Configure the await if the method is not a void return type.
        sb.Append(")").Append((method.IsReturnVoid || forLog) ? ";" : ".ConfigureAwait(false);");

        if (!forLog)
            sb.AppendLine();
    }

    /// <summary>
    /// Emits the proxy method implementation (making calls): arguments are written inline as a MessagePack array
    /// into a pooled buffer that the invoker returns after sending.
    /// </summary>
    public static void EmitProxyMethodInvocation(StringBuilder sb, MethodData method)
    {
        sb.Append("             public ");

        if (method.IsReturnVoid)
        {
            sb.Append("void ");
        }
        else if (method.IsAsync)
        {
            if (method.ReturnType != null)
                sb.Append("global::System.Threading.Tasks.ValueTask<").Append(method.ReturnType).Append("> ");
            else
                sb.Append("global::System.Threading.Tasks.ValueTask ");
        }

        sb.Append(method.Name).Append("(");
        foreach (var parameter in method.Parameters)
            sb.Append(parameter.Type).Append(" ").Append(parameter.Name).Append(", ");

        if (method.Parameters.Length > 0)
            sb.Remove(sb.Length - 2, 2);

        sb.AppendLine(")");
        sb.AppendLine("             {");
        sb.AppendLine("                 var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);");

        var hasArgs = method.SerializedParameterCount > 0;
        if (hasArgs)
        {
            sb.AppendLine("                 var __args = global::NexNet.Serialization.PooledArrayBufferWriter.Rent();");
            sb.AppendLine("                 var __writer = new global::NexNet.Serialization.MsgPackWriter(__args);");
            sb.Append("                 __writer.WriteArrayHeader(").Append(method.SerializedParameterCount).AppendLine(");");
            foreach (var p in method.Parameters)
            {
                if (p.SerializedType == null)
                    continue;
                sb.Append("                 ").AppendLine(PrimitiveCodec.WriteStatement(p.SerializedType, p.SerializedValue!, "__writer"));
            }

            sb.AppendLine("                 __writer.Flush();");
        }

        // Logging
        sb.Append("                 __proxyInvoker.Logger?.ProxyLog($\"Proxy Invoking Method: ");
        sb.Append(method.Name).Append("(");
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var name = method.Parameters[i].Name;
            if (method.Parameters[i].IsCancellationToken)
                sb.Append(name).Append(", ");
            else
                sb.Append(name).Append(" = {").Append(name).Append("}, ");
        }

        if (method.Parameters.Length > 0)
            sb.Remove(sb.Length - 2, 2);

        sb.AppendLine(");\");");
        sb.Append("                 ");

        var argsExpression = hasArgs ? "__args, " : "global::System.Memory<byte>.Empty, ";
        if (method.IsReturnVoid || method.DuplexPipeParameter != null)
        {
            sb.Append(method.DuplexPipeParameter == null ? "_ = " : "return ");
            sb.Append("__proxyInvoker.ProxyInvokeMethodCore(").Append(method.Id).Append(", ");
            sb.Append(argsExpression);
            sb.Append("global::NexNet.Messages.InvocationFlags.")
                .Append(method.DuplexPipeParameter == null ? "None" : "DuplexPipe").AppendLine(");");
        }
        else if (method.IsAsync)
        {
            sb.Append("return __proxyInvoker.ProxyInvokeAndWaitForResultCore");
            if (method.ReturnType != null)
                sb.Append("<").Append(method.ReturnType).Append(">");

            sb.Append("(").Append(method.Id).Append(", ");
            sb.Append(argsExpression);
            sb.Append(method.CancellationTokenParameter != null ? method.CancellationTokenParameter.Name : "null")
                .AppendLine(");");
        }

        sb.AppendLine("             }");
    }

    /// <summary>
    /// Gets a string representation of the method for comments.
    /// </summary>
    public static string ToStringRepresentation(MethodData method)
    {
        var sb = SymbolUtilities.GetStringBuilder();

        if (method.IsReturnVoid)
        {
            sb.Append("void");
        }
        else if (method.IsAsync)
        {
            sb.Append("ValueTask");

            if (method.ReturnArity > 0)
            {
                sb.Append("<").Append(method.ReturnTypeSource).Append(">");
            }
        }

        sb.Append(" ");
        sb.Append(method.Name).Append("(");

        var paramsLength = method.Parameters.Length;
        if (paramsLength > 0)
        {
            for (int i = 0; i < paramsLength; i++)
            {
                sb.Append(method.Parameters[i].TypeSource);
                sb.Append(" ");
                sb.Append(method.Parameters[i].Name);

                if (i + 1 < paramsLength)
                {
                    sb.Append(", ");
                }
            }
        }

        sb.Append(")");

        var stringMethod = sb.ToString();

        SymbolUtilities.ReturnStringBuilder(sb);

        return stringMethod;
    }
}
