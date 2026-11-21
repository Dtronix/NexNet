namespace NexNet.Interop;

/// <summary>
/// How to configure a client pool under test (<see cref="Harness.CreatePoolAsync(ClientTarget, ClientSpec, ClientPoolSpec?)"/>),
/// in every language. The clients of the pool use the <see cref="ClientSpec"/> given with it. Null means the runtime's
/// default. Control protocol §6.11.1.
/// </summary>
/// <remarks>
/// <para>
/// The health check is not configurable: it runs every <c>clamp(maxIdleTimeMs / 4, 100 ms, 30 s)</c> on the clients'
/// clock (the test clock when <see cref="ClientSpec.FakeTime"/> is set), and each pass disposes the idle clients that
/// are not connected, then the idle clients idle for at least <see cref="MaxIdleTimeMs"/> beyond the first
/// <see cref="MinIdleConnections"/> healthy ones (oldest first). It never creates clients.
/// </para>
/// </remarks>
public sealed class ClientPoolSpec
{
    /// <summary>The runtime default of <see cref="MaxConnections"/>.</summary>
    public const int DefaultMaxConnections = 10;

    /// <summary>The runtime default of <see cref="MaxIdleTimeMs"/> (2 minutes).</summary>
    public const int DefaultMaxIdleTimeMs = 120_000;

    /// <summary>The runtime default of <see cref="MinIdleConnections"/>.</summary>
    public const int DefaultMinIdleConnections = 1;

    /// <summary>Clients the pool holds at most, idle and rented together; a rent beyond waits for a return. At least 1.</summary>
    public int? MaxConnections { get; set; }

    /// <summary>How long a client may stay idle before the health check disposes it, in milliseconds. At least 0.</summary>
    public int? MaxIdleTimeMs { get; set; }

    /// <summary>Idle clients the health check keeps past <see cref="MaxIdleTimeMs"/>. At least 0; it may exceed <see cref="MaxConnections"/>.</summary>
    public int? MinIdleConnections { get; set; }

    /// <summary>A copy.</summary>
    public ClientPoolSpec Clone() => (ClientPoolSpec)MemberwiseClone();

    /// <summary>
    /// Checks the ranges, so every row fails the same way: the runner refuses a bad spec before it reaches a client
    /// (a host refuses it with <c>BadRequest</c> as well).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A member is out of range.</exception>
    public void Validate()
    {
        if (MaxConnections is { } max)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(max, 0, nameof(MaxConnections));
        if (MaxIdleTimeMs is { } idle)
            ArgumentOutOfRangeException.ThrowIfNegative(idle, nameof(MaxIdleTimeMs));
        if (MinIdleConnections is { } min)
            ArgumentOutOfRangeException.ThrowIfNegative(min, nameof(MinIdleConnections));
    }
}

/// <summary>What a client pool under test holds (<see cref="IPoolUnderTest.GetStateAsync"/>).</summary>
/// <param name="MaxConnections">The pool's maximum (<see cref="ClientPoolSpec.MaxConnections"/>, or the default).</param>
/// <param name="Idle">Clients in the pool, ready to be rented.</param>
/// <param name="Rented">Rentals not returned yet.</param>
public sealed record ClientPoolState(int MaxConnections, int Idle, int Rented);
