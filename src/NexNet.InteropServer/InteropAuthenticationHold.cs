namespace NexNet.InteropServer;

/// <summary>
/// Holds one of the interop server's authentications (<c>OnAuthenticate</c>) until the test releases it, so a test can
/// act while a client's handshake is in progress: the client has sent its greeting and waits for the server's. Created
/// by <see cref="InteropServerOptions.HoldAuthentication"/>; it works the same for every client language, as the
/// server runs in the test process.
/// </summary>
/// <remarks>
/// Release the hold before the server is disposed (for example in a <c>finally</c>): a held authentication keeps its
/// session open until then.
/// </remarks>
public sealed class InteropAuthenticationHold
{
    private readonly int _authentication;
    private readonly InteropTokenValidator? _validator;
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    /// <summary>Creates a hold of the authentication numbered <paramref name="authentication"/>.</summary>
    /// <param name="authentication">The authentication to hold, counted from 1 over the server's whole life.</param>
    /// <param name="validator">The validator that decides every authentication once it is not (or no longer) held;
    /// null accepts every client with a <see cref="DefaultIdentity"/>.</param>
    internal InteropAuthenticationHold(int authentication, InteropTokenValidator? validator)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(authentication, 1);
        _authentication = authentication;
        _validator = validator;
    }

    /// <summary>The number of authentications the server has started.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Completes when the held authentication has started and waits for <see cref="Release"/>.</summary>
    public Task Held => _held.Task;

    /// <summary>Lets the held authentication go on; an authentication that starts later is not held. Idempotent.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>The validator the server uses: counts each authentication and holds the chosen one.</summary>
    internal async ValueTask<IIdentity?> ValidateAsync(ReadOnlyMemory<byte>? token, NexusEndpointInfo? endpoint)
    {
        if (Interlocked.Increment(ref _count) == _authentication)
        {
            _held.TrySetResult();
            await _released.Task.ConfigureAwait(false);
        }

        return _validator == null
            ? new DefaultIdentity()
            : await _validator(token, endpoint).ConfigureAwait(false);
    }
}
