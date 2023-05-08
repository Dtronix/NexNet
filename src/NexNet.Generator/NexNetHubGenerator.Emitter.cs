﻿using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexNet.Generator;

partial class NexNetHubGenerator
{
    internal static void Generate(TypeDeclarationSyntax syntax, Compilation compilation, IGeneratorContext context)
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

        var hubMeta = new HubMeta(typeSymbol);

        // ReportDiagnostic when validate failed.
        if (!hubMeta.Validate(syntax, context))
        {
            return;
        }

        var fullType = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "")
            .Replace("<", "_")
            .Replace(">", "_");

        var sb = new StringBuilder();

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

        var ns = hubMeta.Symbol.ContainingNamespace;
        if (!ns.IsGlobalNamespace)
        {
            if (context.IsCSharp10OrGreater())
            {
                sb.AppendLine($"namespace {ns};");
            }
            else
            {
                sb.AppendLine($"namespace {ns} {{");
            }
        }
        sb.AppendLine();

        hubMeta.EmitHub(sb);

        sb.AppendLine();

        hubMeta.ProxyInterface.EmitProxyImpl(sb);

        sb.AppendLine();
        
        if (hubMeta.IsClientHub)
        {
            hubMeta.HubInterface.EmitInterfaceWithMethodHash(sb);
            sb.AppendLine();
            hubMeta.ProxyInterface.EmitInterfaceWithMethodHash(sb);
        }
        
        var code = sb.ToString();
        context.AddSource($"{fullType}.NexNetHub.g.cs", code);
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

partial class HubMeta
{
    private string EmitServerClientName() => IsServerHub ? "Server" : "Client";
    public void EmitHub(StringBuilder sb)
    {
        var descriptionText = IsServerHub
            ? "Hub used for handling all client communications."
            : "Hub used for handling all server communications.";
        sb.AppendLine($$"""
/// <summary>
/// {{descriptionText}}
/// </summary>
partial class {{TypeName}} : global::NexNet.Invocation.{{EmitServerClientName()}}HubBase<{{this.ProxyInterface.ProxyImplNameWithNamespace}}>, {{this.HubInterface.Namespace}}.{{this.HubInterface.TypeName}}
{
""");
        if (IsServerHub)
        {
            sb.AppendLine($$"""
    /// <summary>
    /// Creates an instance of the server for this hub and matching client.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="hubFactory">Factory used to instance hubs for the server on each client connection. Useful to pass parameters to the hub.</param>
    /// <returns>NexNetServer for handling incoming connections.</returns>
    public static global::NexNet.NexNetServer<{{this.Namespace}}.{{TypeName}}, {{this.ProxyInterface.ProxyImplNameWithNamespace}}> CreateServer(global::NexNet.Transports.ServerConfig config, global::System.Func<{{this.Namespace}}.{{TypeName}}> hubFactory)
    {
        return new global::NexNet.NexNetServer<{{this.Namespace}}.{{TypeName}}, {{this.ProxyInterface.ProxyImplNameWithNamespace}}>(config, hubFactory);
    }
""");
        }
        else
        {

            sb.AppendLine($$"""
    /// <summary>
    /// Creates an instance of the client for this hub and matching server.
    /// </summary>
    /// <param name="config">Configurations for this instance.</param>
    /// <param name="hub">Hub used for this client while communicating with the server. Useful to pass parameters to the hub.</param>
    /// <returns>NexNetClient for connecting to the matched NexNetServer.</returns>
    public static global::NexNet.NexNetClient<{{this.Namespace}}.{{TypeName}}, {{this.ProxyInterface.ProxyImplNameWithNamespace}}> CreateClient(global::NexNet.Transports.ClientConfig config, {{TypeName}} hub)
    {
        return new global::NexNet.NexNetClient<{{this.Namespace}}.{{TypeName}}, {{this.ProxyInterface.ProxyImplNameWithNamespace}}>(config, hub);
    }
""");
        }

        sb.AppendLine($$"""

    protected override async global::System.Threading.Tasks.ValueTask InvokeMethodCore(global::NexNet.Messages.IInvocationRequestMessage message, global::System.Buffers.IBufferWriter<byte>? returnBuffer)
    {
        global::System.Threading.CancellationTokenSource? cts = null;
        try
        {
            switch (message.MethodId)
            {
""");
        for (int i = 0; i < HubInterface.Methods.Length; i++)
        {
            sb.Append($$"""
                case {{HubInterface.Methods[i].Id}}:
                {

""");
            HubInterface.Methods[i].EmitHubInvocation(sb, this.ProxyInterface);
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
                var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<{{this.ProxyInterface.ProxyImplNameWithNamespace}}>>(this);
                methodInvoker.ReturnCancellationToken(message.InvocationId);
            }
        }

    }
}
""");
    }


}


partial class MethodMeta
{
    public void EmitHubInvocation(StringBuilder sb, InvocationInterfaceMeta proxyImplementation)
    {
        if (CancellationTokenParameter != null)
        {
            sb.AppendLine($"                    var methodInvoker = global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IMethodInvoker<{proxyImplementation.ProxyImplNameWithNamespace}>>(this);");
            sb.AppendLine("                    cts = methodInvoker.RegisterCancellationToken(message.InvocationId);");
        }
        if (ParametersLessCancellation.Length > 0)
        {
            sb.Append("                    var arguments = global::MemoryPack.MemoryPackSerializer.Deserialize<System.ValueTuple<");
            foreach (var methodParameterMeta in ParametersLessCancellation)
            {
                sb.Append(methodParameterMeta.ParamType).Append(", ");
            }

            sb.Remove(sb.Length - 2, 2);
            sb.AppendLine(">>(message.Arguments.Span);");
        }
        sb.Append("                    ");
        if (IsReturnVoid)
        {
            EmitHubMethodInvocation(sb);
        }
        else if (IsAsync)
        {
            if (ReturnType == null)
            {
                sb.Append("await ");
                EmitHubMethodInvocation(sb);
            }
            else
            {
                sb.Append("var result = await ");
                EmitHubMethodInvocation(sb);
                sb.AppendLine("""
                    if (returnBuffer != null)
                        global::MemoryPack.MemoryPackSerializer.Serialize(returnBuffer, result);
""");
            }
        }
    }

    public void EmitHubMethodInvocation(StringBuilder sb)
    {
        sb.Append(this.Name).Append("(");

        for (int i = 0; i < ParametersLessCancellation.Length; i++)
        {
            sb.Append("arguments.Item").Append(i+1).Append(", ");
        }

        if (CancellationTokenParameter != null)
        {
            sb.Append("cts.Token");
        }
        else
        {
            if (ParametersLessCancellation.Length > 0)
            {
                sb.Remove(sb.Length - 2, 2);
            }
        }


        sb.AppendLine(");");
    }

    public void EmitProxyMethodInvocation(StringBuilder sb)
    {
        sb.Append("    public ");

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
        }

        sb.AppendLine(")");
        sb.AppendLine("    {");


        if (ParametersLessCancellation.Length > 0)
        {
            sb.Append("        var arguments = global::MemoryPack.MemoryPackSerializer.Serialize<global::System.ValueTuple<");
            
            foreach (var p in ParametersLessCancellation)
            {
                sb.Append(p.ParamType).Append(", ");
            }

            if (ParametersLessCancellation.Length > 0)
            {
                sb.Remove(sb.Length - 2, 2);
            }

            sb.Append(">>(new(");
            foreach (var p in ParametersLessCancellation)
            {
                sb.Append(p.Name).Append(", ");
            }

            if (ParametersLessCancellation.Length > 0)
            {
                sb.Remove(sb.Length - 2, 2);
            }

            sb.AppendLine("));");

            sb.AppendLine("""

        // Check for arguments which exceed max length.
        if (arguments.Length > global::NexNet.Messages.IInvocationRequestMessage.MaxArgumentSize)
            throw new ArgumentOutOfRangeException(nameof(arguments), arguments.Length, $"Message arguments exceeds maximum size allowed Must be {NexNet.Messages.IInvocationRequestMessage.MaxArgumentSize} bytes or less.");

""");
        }

        sb.Append("        ");

        if (this.IsReturnVoid)
        {
            sb.Append("InvokeMethod(").Append(this.Id).Append(", ");
            sb.AppendLine(ParametersLessCancellation.Length > 0 ? "arguments);" : "null);");
        }
        else if (this.IsAsync)
        {
            sb.Append("return InvokeWaitForResult");
            if (this.ReturnType != null)
            {
                sb.Append("<").Append(this.ReturnType).Append(">");
            }

            sb.Append("(").Append(this.Id).Append(", ");
            sb.Append(ParametersLessCancellation.Length > 0 ? "arguments, " : "null, ");
            sb.Append(this.CancellationTokenParameter != null ? CancellationTokenParameter.Name : "null").AppendLine(");");
        }

        sb.AppendLine("    }").AppendLine();
    }


}

partial class InvocationInterfaceMeta
{
    public void EmitProxyImpl(StringBuilder sb)
    {
        sb.AppendLine($$"""
public class {{ProxyImplName}} : global::NexNet.Invocation.ProxyInvocationBase, {{this.Namespace}}.{{this.TypeName}}
{
""");
        foreach (var method in Methods)
        {
            method.EmitProxyMethodInvocation(sb);
        }
        sb.AppendLine($$"""
}
""");
    }


    public void EmitInterfaceWithMethodHash(StringBuilder sb)
    {
        sb.AppendLine($$"""
partial interface {{TypeName}} : global::NexNet.Invocation.IInterfaceMethodHash
{
    static int global::NexNet.Invocation.IInterfaceMethodHash.MethodHash { get => {{GetHash()}}; }
}
""");
    }
}

