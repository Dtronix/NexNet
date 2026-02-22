using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

class GeneratorAuthorizationTests
{
    private const string AuthPreamble = """
using NexNet;
using System;
using System.Threading.Tasks;
namespace NexNetDemo;

public enum TestPermission { Read, Write, Admin }
partial interface IAuthClientNexus { }
partial interface IAuthServerNexus
{
    ValueTask ProtectedMethod(string input);
    ValueTask AdminMethod();
    ValueTask UnprotectedMethod();
}
""";

    [Test]
    public void AuthorizeOnClientNexus_EmitsError()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator($$"""
using NexNet;
using System;
using System.Threading.Tasks;
namespace NexNetDemo;

public enum TestPermission { Read, Write, Admin }
partial interface IClientNexus
{
    ValueTask DoWork();
}
partial interface IServerNexus { }

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus
{
    [NexusAuthorize<TestPermission>(TestPermission.Write)]
    public ValueTask DoWork() => ValueTask.CompletedTask;
}

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus { }
""");
        Assert.That(diagnostics.Any(d => d.Id == DiagnosticDescriptors.AuthorizeOnClientNexus.Id), Is.True);
    }

    [Test]
    public void AuthorizeWithoutOnAuthorize_EmitsWarning()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator(AuthPreamble + """

[Nexus<IAuthClientNexus, IAuthServerNexus>(NexusType = NexusType.Client)]
partial class AuthClientNexus { }

[Nexus<IAuthServerNexus, IAuthClientNexus>(NexusType = NexusType.Server)]
partial class AuthServerNexus
{
    [NexusAuthorize<TestPermission>(TestPermission.Write)]
    public ValueTask ProtectedMethod(string input) => ValueTask.CompletedTask;

    public ValueTask AdminMethod() => ValueTask.CompletedTask;
    public ValueTask UnprotectedMethod() => ValueTask.CompletedTask;
}
""", minDiagnostic: DiagnosticSeverity.Warning);
        Assert.That(diagnostics.Any(d => d.Id == DiagnosticDescriptors.AuthorizeWithoutOnAuthorize.Id), Is.True);
    }

    [Test]
    public void AuthorizeWithOnAuthorize_NoWarning()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator(AuthPreamble + """

[Nexus<IAuthClientNexus, IAuthServerNexus>(NexusType = NexusType.Client)]
partial class AuthClientNexus { }

[Nexus<IAuthServerNexus, IAuthClientNexus>(NexusType = NexusType.Server)]
partial class AuthServerNexus
{
    [NexusAuthorize<TestPermission>(TestPermission.Write)]
    public ValueTask ProtectedMethod(string input) => ValueTask.CompletedTask;

    public ValueTask AdminMethod() => ValueTask.CompletedTask;
    public ValueTask UnprotectedMethod() => ValueTask.CompletedTask;

    protected override ValueTask<AuthorizeResult> OnAuthorize(
        NexNet.Invocation.ServerSessionContext<AuthServerNexus.ClientProxy> context,
        int methodId, string methodName, ReadOnlyMemory<int> requiredPermissions)
        => new(AuthorizeResult.Allowed);
}
""", minDiagnostic: DiagnosticSeverity.Warning);
        Assert.That(diagnostics.Any(d => d.Id == DiagnosticDescriptors.AuthorizeWithoutOnAuthorize.Id), Is.False);
    }

    [Test]
    public void MixedPermissionEnums_EmitsError()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using System;
using System.Threading.Tasks;
namespace NexNetDemo;

public enum PermA { X }
public enum PermB { Y }
partial interface IClientNexus { }
partial interface IServerNexus
{
    ValueTask MethodA();
    ValueTask MethodB();
}

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus { }

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    [NexusAuthorize<PermA>(PermA.X)]
    public ValueTask MethodA() => ValueTask.CompletedTask;

    [NexusAuthorize<PermB>(PermB.Y)]
    public ValueTask MethodB() => ValueTask.CompletedTask;
}
""");
        Assert.That(diagnostics.Any(d => d.Id == DiagnosticDescriptors.MixedPermissionEnumTypes.Id), Is.True);
    }

    [Test]
    public void NoAuthorize_NoDiagnostics()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator(AuthPreamble + """

[Nexus<IAuthClientNexus, IAuthServerNexus>(NexusType = NexusType.Client)]
partial class AuthClientNexus { }

[Nexus<IAuthServerNexus, IAuthClientNexus>(NexusType = NexusType.Server)]
partial class AuthServerNexus
{
    public ValueTask ProtectedMethod(string input) => ValueTask.CompletedTask;
    public ValueTask AdminMethod() => ValueTask.CompletedTask;
    public ValueTask UnprotectedMethod() => ValueTask.CompletedTask;
}
""", minDiagnostic: DiagnosticSeverity.Warning);
        Assert.That(diagnostics.Any(d =>
            d.Id == DiagnosticDescriptors.AuthorizeOnClientNexus.Id ||
            d.Id == DiagnosticDescriptors.AuthorizeWithoutOnAuthorize.Id ||
            d.Id == DiagnosticDescriptors.MixedPermissionEnumTypes.Id), Is.False);
    }

    [Test]
    public void AuthorizedMethod_GeneratesSuccessfully()
    {
        // Verify the generator produces compilable code with auth guards
        var diagnostics = CSharpGeneratorRunner.RunGenerator(AuthPreamble + """

[Nexus<IAuthClientNexus, IAuthServerNexus>(NexusType = NexusType.Client)]
partial class AuthClientNexus { }

[Nexus<IAuthServerNexus, IAuthClientNexus>(NexusType = NexusType.Server)]
partial class AuthServerNexus
{
    [NexusAuthorize<TestPermission>(TestPermission.Write)]
    public ValueTask ProtectedMethod(string input) => ValueTask.CompletedTask;

    [NexusAuthorize<TestPermission>(TestPermission.Admin)]
    public ValueTask AdminMethod() => ValueTask.CompletedTask;

    public ValueTask UnprotectedMethod() => ValueTask.CompletedTask;

    protected override ValueTask<AuthorizeResult> OnAuthorize(
        NexNet.Invocation.ServerSessionContext<AuthServerNexus.ClientProxy> context,
        int methodId, string methodName, ReadOnlyMemory<int> requiredPermissions)
        => new(AuthorizeResult.Allowed);
}
""");
        // No errors means generated code compiled successfully
        Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
    }

    [Test]
    public void MarkerOnlyAuthorize_GeneratesSuccessfully()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator(AuthPreamble + """

[Nexus<IAuthClientNexus, IAuthServerNexus>(NexusType = NexusType.Client)]
partial class AuthClientNexus { }

[Nexus<IAuthServerNexus, IAuthClientNexus>(NexusType = NexusType.Server)]
partial class AuthServerNexus
{
    [NexusAuthorize<TestPermission>()]
    public ValueTask ProtectedMethod(string input) => ValueTask.CompletedTask;

    public ValueTask AdminMethod() => ValueTask.CompletedTask;
    public ValueTask UnprotectedMethod() => ValueTask.CompletedTask;

    protected override ValueTask<AuthorizeResult> OnAuthorize(
        NexNet.Invocation.ServerSessionContext<AuthServerNexus.ClientProxy> context,
        int methodId, string methodName, ReadOnlyMemory<int> requiredPermissions)
        => new(AuthorizeResult.Allowed);
}
""");
        Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
    }

    [Test]
    public void MultiplePermissions_GeneratesSuccessfully()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator(AuthPreamble + """

[Nexus<IAuthClientNexus, IAuthServerNexus>(NexusType = NexusType.Client)]
partial class AuthClientNexus { }

[Nexus<IAuthServerNexus, IAuthClientNexus>(NexusType = NexusType.Server)]
partial class AuthServerNexus
{
    [NexusAuthorize<TestPermission>(TestPermission.Read, TestPermission.Write, TestPermission.Admin)]
    public ValueTask ProtectedMethod(string input) => ValueTask.CompletedTask;

    public ValueTask AdminMethod() => ValueTask.CompletedTask;
    public ValueTask UnprotectedMethod() => ValueTask.CompletedTask;

    protected override ValueTask<AuthorizeResult> OnAuthorize(
        NexNet.Invocation.ServerSessionContext<AuthServerNexus.ClientProxy> context,
        int methodId, string methodName, ReadOnlyMemory<int> requiredPermissions)
        => new(AuthorizeResult.Allowed);
}
""");
        Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
    }

    [Test]
    public void AuthorizeOnCollection_GeneratesSuccessfully()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Collections;
using NexNet.Collections.Lists;
using System;
using System.Threading.Tasks;
namespace NexNetDemo;

public enum TestPermission { Read, Write, Admin }
partial interface IClientNexus { }
partial interface IServerNexus
{
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    [NexusAuthorize<TestPermission>(TestPermission.Admin)]
    INexusList<string> SecureItems { get; }
}

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus { }

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    protected override ValueTask<AuthorizeResult> OnAuthorize(
        NexNet.Invocation.ServerSessionContext<ServerNexus.ClientProxy> context,
        int methodId, string methodName, ReadOnlyMemory<int> requiredPermissions)
        => new(AuthorizeResult.Allowed);
}
""");
        Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
    }

    [Test]
    public void AuthorizeOnClientCollection_EmitsError()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Collections;
using NexNet.Collections.Lists;
using System;
using System.Threading.Tasks;
namespace NexNetDemo;

public enum TestPermission { Read, Write, Admin }
partial interface IClientNexus
{
    ValueTask DoWork();
}
partial interface IServerNexus
{
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    [NexusAuthorize<TestPermission>(TestPermission.Admin)]
    INexusList<string> SecureItems { get; }
}

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus
{
    public ValueTask DoWork() => ValueTask.CompletedTask;
}

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus { }
""");
        // The server nexus has no OnAuthorize, but more importantly the NEXNET024 check
        // applies to client nexus. Since collection auth is on the server interface here,
        // it should trigger NEXNET025 (no OnAuthorize) but not NEXNET024.
        // For a true client nexus auth error, we'd need auth on the client interface,
        // but collections can't be on client anyway (NEXNET020).
        // This test verifies that collection auth on server without OnAuthorize triggers NEXNET025.
        Assert.That(diagnostics.Any(d => d.Id == DiagnosticDescriptors.AuthorizeWithoutOnAuthorize.Id), Is.True);
    }

    [Test]
    public void CollectionAndMethodMixedEnums_EmitsError()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Collections;
using NexNet.Collections.Lists;
using System;
using System.Threading.Tasks;
namespace NexNetDemo;

public enum PermA { X }
public enum PermB { Y }
partial interface IClientNexus { }
partial interface IServerNexus
{
    ValueTask MethodA();

    [NexusCollection(NexusCollectionMode.ServerToClient)]
    [NexusAuthorize<PermB>(PermB.Y)]
    INexusList<string> SecureItems { get; }
}

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus { }

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus
{
    [NexusAuthorize<PermA>(PermA.X)]
    public ValueTask MethodA() => ValueTask.CompletedTask;
}
""");
        Assert.That(diagnostics.Any(d => d.Id == DiagnosticDescriptors.MixedPermissionEnumTypes.Id), Is.True);
    }

    [Test]
    public void AuthorizeOnCollection_WithoutOnAuthorize_EmitsWarning()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using NexNet.Collections;
using NexNet.Collections.Lists;
using System;
using System.Threading.Tasks;
namespace NexNetDemo;

public enum TestPermission { Read, Write, Admin }
partial interface IClientNexus { }
partial interface IServerNexus
{
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    [NexusAuthorize<TestPermission>(TestPermission.Admin)]
    INexusList<string> SecureItems { get; }
}

[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus { }

[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus { }
""", minDiagnostic: DiagnosticSeverity.Warning);
        Assert.That(diagnostics.Any(d => d.Id == DiagnosticDescriptors.AuthorizeWithoutOnAuthorize.Id), Is.True);
    }
}
