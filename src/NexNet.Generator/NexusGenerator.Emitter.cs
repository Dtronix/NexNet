﻿using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexNet.Generator;

partial class NexusGenerator
{
    internal static void Generate(TypeDeclarationSyntax syntax, Compilation compilation, GeneratorContext context)
    {
        var semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);

        var typeSymbol = semanticModel.GetDeclaredSymbol(syntax, context.CancellationToken);
        if (typeSymbol == null)
        {
            return;
        }

        // verify is partial
        if (!IsPartial(syntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MustBePartial, syntax.Identifier.GetLocation(), typeSymbol.Name));
            return;
        }

        // nested is not allowed
        if (IsNested(syntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NestedNotAllow, syntax.Identifier.GetLocation(), typeSymbol.Name));
            return;
        }

        // nested is not allowed
        if (IsNested(syntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NestedNotAllow, syntax.Identifier.GetLocation(), typeSymbol.Name));
            return;
        }

        var nexusMeta = new NexusMeta(typeSymbol);

        // ReportDiagnostic when validate failed.
        if (!nexusMeta.Validate(syntax, context))
        {
            return;
        }

        var fullType = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "")
            .Replace("<", "_")
            .Replace(">", "_");

        var sb = SymbolUtilities.GetStringBuilder();

        /*
        // <auto-generated/>
#nullable enable
#pragma warning disable CS0108 // hides inherited member
#pragma warning disable CS0162 // Unreachable code
#pragma warning disable CS0164 // This label has not been referenced
#pragma warning disable CS0219 // Variable assigned but never used
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8601 // Possible null reference assignment
#pragma warning disable CS8602
#pragma warning disable CS8604 // Possible null reference argument for parameter
#pragma warning disable CS8619
#pragma warning disable CS8620
#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method
#pragma warning disable CS8765 // Nullability of type of parameter
#pragma warning disable CS9074 // The 'scoped' modifier of parameter doesn't match overridden or implemented member
#pragma warning disable CA1050 // Declare types in namespaces.*/
        sb.AppendLine(@"// <auto-generated/>
#nullable enable
");

        nexusMeta.EmitNexus(sb);

        var code = sb.ToString();

        SymbolUtilities.ReturnStringBuilder(sb);

        context.AddSource($"{fullType}.Nexus.g.cs", code);
    }

    static bool IsPartial(TypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
    }

    static bool IsNested(TypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration.Parent is TypeDeclarationSyntax;
    }


}

partial class NexusMeta
{
    private string EmitServerClientName() => NexusAttribute.IsServer ? "Server" : "Client";
    public void EmitNexus(StringBuilder sb)
    {
        sb.AppendLine($$"""
namespace {{Symbol.ContainingNamespace}} 
{
    /// <summary>
    /// Nexus used for handling all {{EmitServerClientName()}} communications.
    /// </summary>
    partial class {{TypeName}} : global::NexNet.Invocation.{{EmitServerClientName()}}NexusBase<{{this.Namespace}}.{{this.TypeName}}.{{this.ProxyInterface.ProxyImplName}}>, {{this.NexusInterface.Namespace}}.{{this.NexusInterface.TypeName}}, global::NexNet.Invocation.IInvocationMethodHash
    {
""");
        if (NexusAttribute.IsServer)
        {
            sb.AppendLine($$"""
        /// <summary>
        /// Creates an instance of the server for this nexus and matching client.
        /// </summary>
        /// <param name="config">Configurations for this instance.</param>
        /// <param name="nexusFactory">Factory used to instance nexuses for the server on each client connection. Useful to pass parameters to the nexus.</param>
        /// <returns>NexusServer for handling incoming connections.</returns>
        public static global::NexNet.NexusServer<{{this.Namespace}}.{{TypeName}}, {{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}> CreateServer(global::NexNet.Transports.ServerConfig config, global::System.Func<{{this.Namespace}}.{{TypeName}}> nexusFactory)
        {
            return new global::NexNet.NexusServer<{{this.Namespace}}.{{TypeName}}, {{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}>(config, nexusFactory);
        }
""");
        }
        else
        {

            sb.AppendLine($$"""
        /// <summary>
        /// Creates an instance of the client for this nexus and matching server.
        /// </summary>
        /// <param name="config">Configurations for this instance.</param>
        /// <param name="nexus">Nexus used for this client while communicating with the server. Useful to pass parameters to the nexus.</param>
        /// <returns>NexusClient for connecting to the matched NexusServer.</returns>
        public static global::NexNet.NexusClient<{{this.Namespace}}.{{TypeName}}, {{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}> CreateClient(global::NexNet.Transports.ClientConfig config, {{TypeName}} nexus)
        {
            return new global::NexNet.NexusClient<{{this.Namespace}}.{{TypeName}}, {{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}>(config, nexus);
        }
""");
        }

        sb.AppendLine($$"""

        protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
        {
            global::System.Threading.CancellationTokenSource? cts = null;
            global::NexNet.Pipes.INexusDuplexPipe? duplexPipe = null;
            var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker>(this);
            try
            {
""");
        if (NexusInterface.Methods.Length > 0)
        {
            sb.AppendLine($$"""
                switch (message.MethodId)
                {
""");


            for (int i = 0; i < NexusInterface.Methods.Length; i++)
            {
                sb.Append($$"""
                    case {{NexusInterface.Methods[i].Id}}:
                    {
                        // {{NexusInterface.Methods[i].ToString()}}

""");
                NexusInterface.Methods[i].EmitNexusInvocation(sb, this.ProxyInterface, this);
                sb.AppendLine("""
                        break;
                    }
""");
            }

            sb.AppendLine($$"""
                }
""");
        }
        else
        {
            sb.AppendLine("                // No methods.");
        }

        sb.AppendLine($$"""
            }
            finally
            {
                if(cts!= null)
                {
                    methodInvoker.ReturnCancellationToken(message.InvocationId);
                }

                if (duplexPipe != null)
                {
                    await methodInvoker.ReturnDuplexPipe(duplexPipe);
                }
            }

        }

        /// <summary>
        /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
        /// </summary>
        static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => {{this.NexusInterface.GetHash()}}; }
""");
        
        ProxyInterface.EmitProxyImpl(sb);

        sb.AppendLine($$"""
    }
}
""");
    }


}


///
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
            sb.AppendLine(");");
        }

        sb.Append("                        this.Context.Logger?.Log((this.Context.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.LocalInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, this.Context.Logger.Category, null, $\"Invoking Method: ");

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

        sb.Append(");");

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
        sb.Append("                 __proxyInvoker.Logger?.Log((__proxyInvoker.Logger.Behaviors & global::NexNet.Logging.NexusLogBehaviors.ProxyInvocationsLogAsInfo) != 0 ? global::NexNet.Logging.NexusLogLevel.Information : global::NexNet.Logging.NexusLogLevel.Debug, __proxyInvoker.Logger.Category, null, $\"Proxy Invoking Method: ");
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


}

partial class InvocationInterfaceMeta
{
    public void EmitProxyImpl(StringBuilder sb)
    {
        sb.AppendLine($$"""

        /// <summary>
        /// Proxy invocation implementation for the matching nexus.
        /// </summary>
        public class {{ProxyImplName}} : global::NexNet.Invocation.ProxyInvocationBase, {{this.Namespace}}.{{this.TypeName}}, global::NexNet.Invocation.IInvocationMethodHash
        {
""");
        foreach (var method in Methods)
        {
            method.EmitProxyMethodInvocation(sb);
        }


        sb.AppendLine($$"""

            /// <summary>
            /// Hash for this the methods on this proxy or nexus.  Used to perform a simple client and server match check.
            /// </summary>
            static int global::NexNet.Invocation.IInvocationMethodHash.MethodHash { get => {{GetHash()}}; }
        }
""");
    }

}

