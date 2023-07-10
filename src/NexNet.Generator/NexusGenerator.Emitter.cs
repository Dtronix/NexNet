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
    partial class {{TypeName}} : global::NexNet.Invocation.{{EmitServerClientName()}}NexusBase<{{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}>, {{this.NexusInterface.Namespace}}.{{this.NexusInterface.TypeName}}, global::NexNet.Invocation.IInvocationMethodHash
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
            global::NexNet.NexusPipe? pipe = null;
            var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<{{this.Namespace}}.{{TypeName}}.{{this.ProxyInterface.ProxyImplName}}>>(this);
            try
            {
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
            }
            finally
            {
                if(cts!= null)
                {
                    methodInvoker.ReturnCancellationToken(message.InvocationId);
                }

                if (pipe != null)
                {
                    await methodInvoker.ReturnPipeReader(message.InvocationId);
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


partial class MethodMeta
{
    public void EmitNexusInvocation(StringBuilder sb, InvocationInterfaceMeta proxyImplementation, NexusMeta nexus)
    {
        if (CancellationTokenParameter != null)
        {
            //sb.AppendLine($"                        var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<{nexus.Namespace}.{nexus.TypeName}.{proxyImplementation.ProxyImplName}>>(this);");
            sb.AppendLine("                        cts = methodInvoker.RegisterCancellationToken(message.InvocationId);");
        }

        if (PipeParameter != null)
        {
            sb.Append("                        pipe = await methodInvoker.RegisterPipeReader(message.InvocationId, ");
            sb.Append(CancellationTokenParameter != null ? "cts.Token" : "null");
            sb.AppendLine(");");
        }

        if (SerializedParameters > 0)
        {
            sb.Append("                        var arguments = message.DeserializeArguments<global::System.ValueTuple<");
            for (var i = 0; i < Parameters.Length; i++)
            {
                if (!Parameters[i].IsSerialized)
                    continue;

                sb.Append(Parameters[i].ParamType).Append(", ");
            }
            sb.Remove(sb.Length - 2, 2);

            sb.AppendLine(">>();");
        }
        sb.Append("                        ");

        if (IsReturnVoid)
        {
            EmitNexusMethodInvocation(sb);
        }
        else if (IsAsync)
        {
            if (IsAsync && ReturnType == null)
            {
                sb.Append("await ");
                EmitNexusMethodInvocation(sb);
            }
            else
            {
                sb.Append("var result = await ");
                EmitNexusMethodInvocation(sb);
                sb.AppendLine("""
                        if (returnBuffer != null)
                            global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
""");
            }
        }
    }

    public void EmitNexusMethodInvocation(StringBuilder sb)
    {
        sb.Append(this.Name).Append("(");

        var serializedParamNumber = 1;
        bool addedParam = false;
        foreach (var methodParameterMeta in Parameters)
        {
            if (methodParameterMeta.IsPipe)
            {
                sb.Append("pipe, ");
                addedParam = true;
            }
            else if (methodParameterMeta.IsSerialized)
            {
                sb.Append("arguments.Item").Append(serializedParamNumber++).Append(", ");
                addedParam = true;
            }
        }

        if (CancellationTokenParameter != null)
        {
            sb.Append("cts.Token");
        }
        else
        {
            if(addedParam)
                sb.Remove(sb.Length - 2, 2);
        }


        sb.AppendLine(");");
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

        /*
        foreach (var p in ParametersLessCancellation)
        {
            sb.Append(p.ParamType).Append(" ").Append(p.Name).Append(", ");
        }

        if (this.CancellationTokenParameter != null)
        {
            sb.Append(this.CancellationTokenParameter.ParamType).Append(" ").Append(this.CancellationTokenParameter.Name);
        }
        else
        {
            if (ParametersLessCancellation.Length > 0)
            {
                sb.Remove(sb.Length - 2, 2);
            }
        }*/

        sb.AppendLine(")");
        sb.AppendLine("             {");

        if (SerializedParameters > 0)
        {
            sb.Append("                 var arguments = base.SerializeArgumentsCore<global::System.ValueTuple<");
            
            foreach (var p in Parameters)
            {
                if(!p.IsSerialized)
                    continue;

                sb.Append(p.ParamType).Append(", ");
            }

            sb.Remove(sb.Length - 2, 2);

            sb.Append(">>(new(");
            foreach (var p in Parameters)
            {
                if (!p.IsSerialized)
                    continue;

                sb.Append(p.Name).Append(", ");
            }

            sb.Remove(sb.Length - 2, 2);

            sb.AppendLine("));");
        }

        sb.Append("                 ");

        if (this.IsReturnVoid)
        {
            sb.Append("_ = ProxyInvokeMethodCore(").Append(this.Id).Append(", ");
            sb.AppendLine(SerializedParameters > 0 ? "arguments);" : "null);");
        }
        else if (this.IsAsync)
        {
            sb.Append("return ProxyInvokeAndWaitForResultCore");
            if (this.ReturnType != null)
            {
                sb.Append("<").Append(this.ReturnType).Append(">");
            }

            sb.Append("(").Append(this.Id).Append(", "); // methodId
            sb.Append(SerializedParameters > 0 ? "arguments, " : "null, "); // arguments
            sb.Append(PipeParameter != null ? PipeParameter.Name : "null").Append(", "); // pipe
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

