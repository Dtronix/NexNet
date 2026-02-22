using System;

namespace NexNet;

/// <summary>
/// Marks a server nexus method as requiring authorization. The generator emits an auth guard
/// that calls <c>OnAuthorize</c> before the method body executes.
/// </summary>
/// <typeparam name="TPermission">Enum type representing the permission set.</typeparam>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public class NexusAuthorizeAttribute<TPermission> : Attribute
    where TPermission : struct, Enum
{
    /// <summary>
    /// The permissions required to invoke this method.
    /// An empty array means "requires auth, no specific permission."
    /// </summary>
    public TPermission[] Permissions { get; }

    /// <summary>
    /// Marks the method as requiring authorization with the specified permissions.
    /// </summary>
    /// <param name="permissions">Zero or more permissions required.</param>
    public NexusAuthorizeAttribute(params TPermission[] permissions)
    {
        Permissions = permissions;
    }
}
