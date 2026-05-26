using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace NexNet.Testing.Authentication;

/// <summary>
/// Registry that the host installs as <c>ServerConfig.OnAuthenticateOverride</c>. Maps opaque
/// token bytes (issued by <see cref="IssueToken"/>) back to the original
/// <see cref="IIdentity"/>. When a client connects with a registered token, the server-side
/// override produces the corresponding identity; unknown tokens resolve to null and the
/// server drops the connection through the existing auth path.
/// </summary>
/// <remarks>
/// Tokens are not garbage-collected: a long-lived host that issues many tokens accumulates them
/// for the host's lifetime. This is by design — the store doesn't know which tokens are still
/// in use, and test scenarios that need long-lived hosts can call <see cref="Clear"/> between
/// scenarios. Typical per-test-method host construction releases the store when the host is
/// disposed.
/// </remarks>
internal sealed class TestAuthenticationStore
{
    private readonly ConcurrentDictionary<string, IIdentity> _byTokenKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Records an identity under a fresh opaque token and returns the bytes the client will
    /// send as its authentication payload.
    /// </summary>
    public Memory<byte> IssueToken(IIdentity identity)
    {
        var key = Guid.NewGuid().ToString("N");
        _byTokenKey[key] = identity;
        return System.Text.Encoding.UTF8.GetBytes(key);
    }

    /// <summary>The delegate to install on <c>ServerConfig.OnAuthenticateOverride</c>.</summary>
    public Func<ReadOnlyMemory<byte>?, ValueTask<IIdentity?>> OverrideDelegate => token =>
    {
        if (token is null || token.Value.IsEmpty)
            return new ValueTask<IIdentity?>((IIdentity?)null);
        var key = System.Text.Encoding.UTF8.GetString(token.Value.Span);
        return _byTokenKey.TryGetValue(key, out var id)
            ? new ValueTask<IIdentity?>(id)
            : new ValueTask<IIdentity?>((IIdentity?)null);
    };

    /// <summary>
    /// Removes every issued token. Long-lived hosts that issue many tokens across scenarios can
    /// call this between scenarios to bound memory; tokens issued before <c>Clear</c> stop
    /// resolving and subsequent connect attempts with those tokens are rejected as if they were
    /// never issued.
    /// </summary>
    public void Clear() => _byTokenKey.Clear();
}
