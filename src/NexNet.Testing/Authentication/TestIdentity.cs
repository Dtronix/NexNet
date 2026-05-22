using System;
using System.Collections.Generic;

namespace NexNet.Testing;

/// <summary>
/// Lightweight <see cref="IIdentity"/> backed by an in-memory role list. The host's
/// authentication-override delegate uses these to populate connections issued via
/// <c>NexusTestHost.ConnectAsAsync</c>.
/// </summary>
public sealed class TestIdentity : IIdentity
{
    /// <inheritdoc />
    public string? DisplayName { get; }

    /// <summary>Roles attached to the identity.</summary>
    public IReadOnlyList<string> Roles { get; }

    private TestIdentity(string name, IReadOnlyList<string> roles)
    {
        DisplayName = name;
        Roles = roles;
    }

    /// <summary>
    /// Constructs a fake identity with the given name and (optional) roles. Used by tests to
    /// pass arbitrary identities through the harness's authentication override without
    /// touching the user's real auth code.
    /// </summary>
    public static TestIdentity Of(string name, params string[] roles)
        => new TestIdentity(name, roles ?? Array.Empty<string>());

    /// <summary>True when this identity carries <paramref name="role"/>.</summary>
    public bool IsInRole(string role)
    {
        foreach (var r in Roles)
            if (string.Equals(r, role, StringComparison.Ordinal))
                return true;
        return false;
    }
}
