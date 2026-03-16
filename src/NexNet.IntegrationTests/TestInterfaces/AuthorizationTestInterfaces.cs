using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Invocation;

#pragma warning disable CS8618
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.TestInterfaces;

public enum TestPermission { Read, Write, Admin }

public partial interface IAuthServerNexus
{
    ValueTask ProtectedMethod(string input);
    ValueTask AdminMethod();
    ValueTask UnprotectedMethod();
    ValueTask MarkerOnlyMethod();
    ValueTask<int> ProtectedWithReturn(int value);
    ValueTask MultiPermissionMethod(string input);
    ValueTask CachedMethod();
    ValueTask NeverCachedMethod();

    [NexusCollection(NexusCollectionMode.ServerToClient)]
    [NexusAuthorize<TestPermission>(TestPermission.Read)]
    INexusList<string> ProtectedList { get; }

    [NexusCollection(NexusCollectionMode.ServerToClient)]
    INexusList<string> UnprotectedList { get; }
}

public partial interface IAuthClientNexus { }

[Nexus<IAuthServerNexus, IAuthClientNexus>(NexusType = NexusType.Server)]
public partial class AuthServerNexus
{
    public Func<ServerSessionContext<AuthServerNexus.ClientProxy>, int, string, ReadOnlyMemory<int>, ValueTask<AuthorizeResult>>? OnAuthorizeHandler;

    public Func<AuthServerNexus, string, ValueTask>? ProtectedMethodHandler;
    public Func<AuthServerNexus, ValueTask>? AdminMethodHandler;
    public Func<AuthServerNexus, ValueTask>? UnprotectedMethodHandler;
    public Func<AuthServerNexus, ValueTask>? MarkerOnlyMethodHandler;
    public Func<AuthServerNexus, int, ValueTask<int>>? ProtectedWithReturnHandler;
    public Func<AuthServerNexus, string, ValueTask>? MultiPermissionMethodHandler;
    public Func<AuthServerNexus, ValueTask>? CachedMethodHandler;
    public Func<AuthServerNexus, ValueTask>? NeverCachedMethodHandler;
    public Action? InvalidateAllAction;
    public Action<int>? InvalidateMethodAction;

    public void CallInvalidateAll() => InvalidateAuthorizationCache();
    public void CallInvalidateMethod(int methodId) => InvalidateAuthorizationCache(methodId);

    [NexusAuthorize<TestPermission>(TestPermission.Write)]
    public ValueTask ProtectedMethod(string input)
    {
        return ProtectedMethodHandler?.Invoke(this, input) ?? ValueTask.CompletedTask;
    }

    [NexusAuthorize<TestPermission>(TestPermission.Admin)]
    public ValueTask AdminMethod()
    {
        return AdminMethodHandler?.Invoke(this) ?? ValueTask.CompletedTask;
    }

    public ValueTask UnprotectedMethod()
    {
        return UnprotectedMethodHandler?.Invoke(this) ?? ValueTask.CompletedTask;
    }

    [NexusAuthorize<TestPermission>()]
    public ValueTask MarkerOnlyMethod()
    {
        return MarkerOnlyMethodHandler?.Invoke(this) ?? ValueTask.CompletedTask;
    }

    [NexusAuthorize<TestPermission>(TestPermission.Read)]
    public ValueTask<int> ProtectedWithReturn(int value)
    {
        return ProtectedWithReturnHandler?.Invoke(this, value) ?? new ValueTask<int>(0);
    }

    [NexusAuthorize<TestPermission>(TestPermission.Read, TestPermission.Admin)]
    public ValueTask MultiPermissionMethod(string input)
    {
        return MultiPermissionMethodHandler?.Invoke(this, input) ?? ValueTask.CompletedTask;
    }

    [NexusAuthorize<TestPermission>(TestPermission.Read, CacheDurationSeconds = 2)]
    public ValueTask CachedMethod()
    {
        return CachedMethodHandler?.Invoke(this) ?? ValueTask.CompletedTask;
    }

    [NexusAuthorize<TestPermission>(TestPermission.Read, CacheDurationSeconds = 0)]
    public ValueTask NeverCachedMethod()
    {
        return NeverCachedMethodHandler?.Invoke(this) ?? ValueTask.CompletedTask;
    }

    protected override ValueTask<AuthorizeResult> OnAuthorize(
        ServerSessionContext<ClientProxy> context,
        int methodId,
        string methodName,
        ReadOnlyMemory<int> requiredPermissions)
    {
        return OnAuthorizeHandler?.Invoke(context, methodId, methodName, requiredPermissions)
               ?? new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
    }
}

[Nexus<IAuthClientNexus, IAuthServerNexus>(NexusType = NexusType.Client)]
public partial class AuthClientNexus { }
