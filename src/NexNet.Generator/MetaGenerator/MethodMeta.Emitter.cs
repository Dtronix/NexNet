using System.Text;

namespace NexNet.Generator.MetaGenerator;


partial class MethodMeta
{
    /// <summary>
    /// Emit the code for the the nexus.
    /// </summary>
    /// <param name="sb"></param>
    /// <param name="proxyImplementation"></param>
    /// <param name="nexus"></param>
    public void EmitNexusInvocation(StringBuilder sb, InvocationInterfaceMeta proxyImplementation, NexusMeta nexus)
    {
        // Create the cancellation token parameter.
        if (CancellationTokenParameter != null)
        {
            //sb.AppendLine($"                        var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<{nexus.Namespace}.{nexus.TypeName}.{proxyImplementation.ProxyImplName}>>(this);");
            sb.AppendLine("                        cts = methodInvoker.RegisterCancellationToken(message.InvocationId);");
        }

        // Deserialize the arguments.
        if (SerializedParameters > 0)
        {
            sb.Append("                        var arguments = message.DeserializeArguments<global::System.ValueTuple<");
            for (var i = 0; i < Parameters.Length; i++)
            {
                if (Parameters[i].SerializedType == null)
                    continue;

                sb.Append(Parameters[i].SerializedType).Append(", ");
            }
            sb.Remove(sb.Length - 2, 2);

            sb.AppendLine(">>();");
        }

        // Register the duplex pipe if we have one.
        if (UtilizesPipes)
        {
            sb.Append("                        duplexPipe = await methodInvoker.RegisterDuplexPipe(arguments.Item");
            sb.Append(DuplexPipeParameter!.SerializedId);
            sb.AppendLine(").ConfigureAwait(false);");
        }

        sb.Append("                        this.Context.Logger?.NexusLog(\"");
        EmitNexusMethodInvocation(sb, true);
        sb.AppendLine("\");");
        sb.Append("                        ");
        
        // Ignore the return value if we are a void method or a duplex pipe method
        if (IsReturnVoid)
        {
            EmitNexusMethodInvocation(sb, false);
        }
        else if (IsAsync)
        {
            // If we are async, we need to await the method invocation and then serialize the return value otherwise
            // we can just invoke the method and serialize the return value
            if (IsAsync && ReturnType == null)
            {
                sb.Append("await ");
                EmitNexusMethodInvocation(sb, false);
            }
            else
            {
                sb.Append("var result = await ");
                EmitNexusMethodInvocation(sb, false);
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
    /// <param name="sb"></param>
    /// <param name="forLog">Change the output to write the output params. Used for logging.</param>
    public void EmitNexusMethodInvocation(StringBuilder sb, bool forLog)
    {
        sb.Append(this.Name).Append("(");

        
        bool addedParam = false;
        foreach (var methodParameterMeta in Parameters)
        {
            // If we have a duplex pipe, we need to pass it in the correct parameter position,
            // otherwise we need to pass the serialized value.
            if (methodParameterMeta.IsDuplexPipe)
            {
                if (forLog)
                {
                    sb.Append(methodParameterMeta.Name)
                        .Append(" = {arguments.Item")
                        .Append(DuplexPipeParameter!.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append("duplexPipe, ");
                }

                addedParam = true;
            }
            else if (methodParameterMeta.IsDuplexUnmanagedChannel)
            {
                if (forLog)
                {
                    sb.Append(methodParameterMeta.Name)
                        .Append(" = {arguments.Item")
                        .Append(DuplexPipeParameter!.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append("global::NexNet.Pipes.NexusDuplexPipeExtensions.GetUnmanagedChannel<");
                    sb.Append(methodParameterMeta.ChannelType);
                    sb.Append(">(duplexPipe), ");
                }

                addedParam = true;
            }
            else if (methodParameterMeta.IsDuplexChannel)
            {
                if (forLog)
                {
                    sb.Append(methodParameterMeta.Name)
                        .Append(" = {arguments.Item")
                        .Append(DuplexPipeParameter!.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append("global::NexNet.Pipes.NexusDuplexPipeExtensions.GetChannel<");
                    sb.Append(methodParameterMeta.ChannelType);
                    sb.Append(">(duplexPipe), ");
                }

                addedParam = true;
            }
            else if (methodParameterMeta.SerializedValue != null)
            {
                if (forLog)
                {
                    sb.Append(methodParameterMeta.Name)
                        .Append(" = {arguments.Item")
                        .Append(methodParameterMeta.SerializedId)
                        .Append("}, ");
                }
                else
                {
                    sb.Append("arguments.Item").Append(methodParameterMeta.SerializedId).Append(", ");
                }

                addedParam = true;
            }
        }

        if (CancellationTokenParameter != null)
        {
            if (forLog)
            {
                sb.Append(CancellationTokenParameter.Name).Append(" = ct");
            }
            else
            {
                sb.Append("cts.Token");
            }
        }
        else
        {
            if(addedParam)
                sb.Remove(sb.Length - 2, 2);
        }
        
        // Configure the await if the method is not a void return type.
        sb.Append(")").Append((this.IsReturnVoid || forLog) ? ";" : ".ConfigureAwait(false);");

        if (!forLog)
            sb.AppendLine();
    }

    public void EmitProxyMethodInvocation(StringBuilder sb)
    {
        sb.Append("             public ");

        if (this.IsReturnVoid)
        {
            sb.Append("void ");
        }
        else if (this.IsAsync)
        {
            if (this.ReturnType != null)
            {
                sb.Append("global::System.Threading.Tasks.ValueTask<").Append(this.ReturnType).Append("> ");
            }
            else
            {
                sb.Append("global::System.Threading.Tasks.ValueTask ");
            }
        }

        sb.Append(this.Name).Append("(");

        foreach (var parameter in Parameters)
        {
            sb.Append(parameter.ParamType).Append(" ").Append(parameter.Name).Append(", ");
        }

        if(Parameters.Length > 0)
            sb.Remove(sb.Length - 2, 2);
        
        sb.AppendLine(")");
        sb.AppendLine("             {");
        sb.AppendLine("                 var __proxyInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this);");
        if (SerializedParameters > 0)
        {
            sb.Append("                 var __proxyInvocationArguments = new global::System.ValueTuple<");
            
            
            foreach (var p in Parameters)
            {
                if(p.SerializedType == null)
                    continue;

                sb.Append(p.SerializedType).Append(", ");
            }

            sb.Remove(sb.Length - 2, 2);

            sb.Append(">(");
            foreach (var p in Parameters)
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
        sb.Append(this.Name).Append("(");
        for (var i = 0; i < Parameters.Length; i++)
        {
            if (Parameters[i].IsCancellationToken)
            {
                sb.Append(Parameters[i].Name)
                    .Append(", ");
            }
            else
            {
                sb.Append(Parameters[i].Name)
                    .Append(" = ")
                    .Append("{__proxyInvocationArguments.Item").Append(i + 1)
                    .Append("}, ");
            }
        }

        if (Parameters.Length > 0) 
            sb.Remove(sb.Length - 2, 2);
        
        sb.AppendLine(");\");");
        sb.Append("                 ");


        if (this.IsReturnVoid || this.DuplexPipeParameter != null)
        {
            // If we are a void method, we need to invoke the method and then ignore the return
            // If we have a duplex pipe parameter, we need to invoke the method and then return the invocation result.
            sb.Append(this.DuplexPipeParameter == null ? "_ = " : "return ");

            sb.Append("__proxyInvoker.ProxyInvokeMethodCore(").Append(this.Id).Append(", ");
            sb.Append(SerializedParameters > 0 ? "__proxyInvocationArguments, " : "null, ");

            // If we have a duplex pipe parameter, we need to pass the duplex pipe invocation flag.
            sb.Append("global::NexNet.Messages.InvocationFlags.").Append(this.DuplexPipeParameter == null ? "None" : "DuplexPipe").AppendLine(");");
        }
        else if (this.IsAsync)
        {
            sb.Append("return __proxyInvoker.ProxyInvokeAndWaitForResultCore");
            if (this.ReturnType != null)
            {
                sb.Append("<").Append(this.ReturnType).Append(">");
            }

            sb.Append("(").Append(this.Id).Append(", "); // methodId
            sb.Append(SerializedParameters > 0 ? "__proxyInvocationArguments, " : "null, "); // arguments
            sb.Append(CancellationTokenParameter != null ? CancellationTokenParameter.Name : "null").AppendLine(");");
        }

        sb.AppendLine("             }");
    }
    
    public override string ToString()
    {
        var sb = SymbolUtilities.GetStringBuilder();

        if (IsReturnVoid)
        {
            sb.Append("void");
        }
        else if (IsAsync)
        {
            sb.Append("ValueTask");

            if (this.ReturnArity > 0)
            {
                sb.Append("<").Append(this.ReturnTypeSource).Append(">");
            }
        }

        sb.Append(" ");
        sb.Append(this.Name).Append("(");

        var paramsLength = this.Parameters.Length;
        if (paramsLength > 0)
        {
            for (int i = 0; i < paramsLength; i++)
            {
                sb.Append(Parameters[i].ParamTypeSource);
                sb.Append(" ");
                sb.Append(Parameters[i].Name);

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
