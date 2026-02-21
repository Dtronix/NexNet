using System.Text;
using NexNet.Generator.Models;

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
        // Emit auth guard before any deserialization
        if (method.AuthorizeData != null)
        {
            sb.AppendLine($$"""
                        var __authResult = await this.Authorize(
                            {{method.Id}},
                            "{{method.Name}}",
                            __authPerms_{{method.Name}}).ConfigureAwait(false);

                        if (__authResult == global::NexNet.AuthorizeResult.Disconnect)
                        {
                            await this.Context.Session.DisconnectAsync(global::NexNet.Messages.DisconnectReason.Unauthorized).ConfigureAwait(false);
                            return;
                        }

                        if (__authResult == global::NexNet.AuthorizeResult.Unauthorized)
                        {
                            if (returnBuffer != null)
                            {
                                await this.SendUnauthorizedResult(message.InvocationId).ConfigureAwait(false);
                            }
                            return;
                        }
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
            sb.Append("                        var arguments = message.DeserializeArguments<global::System.ValueTuple<");
            for (var i = 0; i < method.Parameters.Length; i++)
            {
                if (method.Parameters[i].SerializedType == null)
                    continue;

                sb.Append(method.Parameters[i].SerializedType).Append(", ");
            }

            sb.Remove(sb.Length - 2, 2);
            sb.AppendLine(">>();");
        }

        // Register the duplex pipe if we have one.
        if (method.UtilizesPipes)
        {
            sb.Append("                        duplexPipe = await methodInvoker.RegisterDuplexPipe(arguments.Item");
            sb.Append(method.DuplexPipeParameter!.SerializedId);
            sb.AppendLine(").ConfigureAwait(false);");
        }

        sb.Append("                        this.Context.Logger?.NexusLog(\"");
        EmitNexusMethodInvocation(sb, method, true);
        sb.AppendLine("\");");
        sb.Append("                        ");

        // Ignore the return value if we are a void method or a duplex pipe method
        if (method.IsReturnVoid)
        {
            EmitNexusMethodInvocation(sb, method, false);
        }
        else if (method.IsAsync)
        {
            // If we are async, we need to await the method invocation and then serialize the return value otherwise
            // we can just invoke the method and serialize the return value
            if (method.IsAsync && method.ReturnType == null)
            {
                sb.Append("await ");
                EmitNexusMethodInvocation(sb, method, false);
            }
            else
            {
                sb.Append("var result = await ");
                EmitNexusMethodInvocation(sb, method, false);
                sb.AppendLine("""
                        if (returnBuffer != null)
                            global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
""");
            }
        }
    }

    /// <summary>
    /// Emits the invocation of the method on the nexus.
    /// </summary>
    /// <param name="sb">StringBuilder to append to.</param>
    /// <param name="method">Method data.</param>
    /// <param name="forLog">Change the output to write the output params. Used for logging.</param>
    private static void EmitNexusMethodInvocation(StringBuilder sb, MethodData method, bool forLog)
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
                        .Append(" = {arguments.Item")
                        .Append(method.DuplexPipeParameter!.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append("duplexPipe, ");
                }

                addedParam = true;
            }
            else if (param.IsDuplexUnmanagedChannel)
            {
                if (forLog)
                {
                    sb.Append(param.Name)
                        .Append(" = {arguments.Item")
                        .Append(method.DuplexPipeParameter!.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append("global::NexNet.Pipes.NexusDuplexPipeExtensions.GetUnmanagedChannel<");
                    sb.Append(param.ChannelType);
                    sb.Append(">(duplexPipe), ");
                }

                addedParam = true;
            }
            else if (param.IsDuplexChannel)
            {
                if (forLog)
                {
                    sb.Append(param.Name)
                        .Append(" = {arguments.Item")
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
                        .Append(" = {arguments.Item")
                        .Append(param.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append("arguments.Item").Append(param.SerializedId).Append(", ");
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
    /// Emits the proxy method implementation (making calls).
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
            {
                sb.Append("global::System.Threading.Tasks.ValueTask<").Append(method.ReturnType).Append("> ");
            }
            else
            {
                sb.Append("global::System.Threading.Tasks.ValueTask ");
            }
        }

        sb.Append(method.Name).Append("(");

        foreach (var parameter in method.Parameters)
        {
            sb.Append(parameter.Type).Append(" ").Append(parameter.Name).Append(", ");
        }

        if (method.Parameters.Length > 0)
            sb.Remove(sb.Length - 2, 2);

        sb.AppendLine(")");
        sb.AppendLine("             {");
        sb.AppendLine("                 var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);");

        if (method.SerializedParameterCount > 0)
        {
            sb.Append("                 var __proxyInvocationArguments = new global::System.ValueTuple<");

            foreach (var p in method.Parameters)
            {
                if (p.SerializedType == null)
                    continue;

                sb.Append(p.SerializedType).Append(", ");
            }

            sb.Remove(sb.Length - 2, 2);

            sb.Append(">(");
            foreach (var p in method.Parameters)
            {
                if (p.SerializedValue == null)
                    continue;

                sb.Append(p.SerializedValue).Append(", ");
            }

            sb.Remove(sb.Length - 2, 2);

            sb.AppendLine(");");
        }

        // Logging
        sb.Append("                 __proxyInvoker.Logger?.ProxyLog($\"Proxy Invoking Method: ");
        sb.Append(method.Name).Append("(");
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (method.Parameters[i].IsCancellationToken)
            {
                sb.Append(method.Parameters[i].Name).Append(", ");
            }
            else
            {
                sb.Append(method.Parameters[i].Name)
                    .Append(" = ")
                    .Append("{__proxyInvocationArguments.Item").Append(i + 1)
                    .Append("}, ");
            }
        }

        if (method.Parameters.Length > 0)
            sb.Remove(sb.Length - 2, 2);

        sb.AppendLine(");\");");
        sb.Append("                 ");

        if (method.IsReturnVoid || method.DuplexPipeParameter != null)
        {
            // If we are a void method, we need to invoke the method and then ignore the return
            // If we have a duplex pipe parameter, we need to invoke the method and then return the invocation result.
            sb.Append(method.DuplexPipeParameter == null ? "_ = " : "return ");

            sb.Append("__proxyInvoker.ProxyInvokeMethodCore(").Append(method.Id).Append(", ");
            sb.Append(method.SerializedParameterCount > 0 ? "__proxyInvocationArguments, " : "null, ");

            // If we have a duplex pipe parameter, we need to pass the duplex pipe invocation flag.
            sb.Append("global::NexNet.Messages.InvocationFlags.")
                .Append(method.DuplexPipeParameter == null ? "None" : "DuplexPipe").AppendLine(");");
        }
        else if (method.IsAsync)
        {
            sb.Append("return __proxyInvoker.ProxyInvokeAndWaitForResultCore");
            if (method.ReturnType != null)
            {
                sb.Append("<").Append(method.ReturnType).Append(">");
            }

            sb.Append("(").Append(method.Id).Append(", "); // methodId
            sb.Append(method.SerializedParameterCount > 0 ? "__proxyInvocationArguments, " : "null, "); // arguments
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
