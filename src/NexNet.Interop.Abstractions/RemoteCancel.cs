using System;
using System.Threading;

namespace NexNet.Interop;

/// <summary>What a <see cref="RemoteCancel"/> is.</summary>
public enum RemoteCancelKind : byte
{
    /// <summary>The invocation is never canceled.</summary>
    None,

    /// <summary>A timer in the language host cancels the invocation (<see cref="RemoteCancel.After(int)"/>).</summary>
    After,

    /// <summary>A local token; the driver forwards its cancellation to the host (<see cref="RemoteCancel.FromToken"/>).</summary>
    Token,
}

/// <summary>
/// The cancellation of a remote invocation. Use <see cref="After(int)"/> to time a cancellation inside the language
/// host (so the race under test is not delayed by the control channel), or pass a <see cref="CancellationToken"/>
/// (implicitly converted) to cancel from the test.
/// </summary>
public sealed class RemoteCancel
{
    private RemoteCancel(RemoteCancelKind kind, int afterMilliseconds, CancellationToken token)
    {
        Kind = kind;
        AfterMilliseconds = afterMilliseconds;
        Token = token;
    }

    /// <summary>Never canceled.</summary>
    public static RemoteCancel None { get; } = new(RemoteCancelKind.None, 0, CancellationToken.None);

    /// <summary>What this cancellation is.</summary>
    public RemoteCancelKind Kind { get; }

    /// <summary>For <see cref="RemoteCancelKind.After"/>: the host-side delay in milliseconds.</summary>
    public int AfterMilliseconds { get; }

    /// <summary>For <see cref="RemoteCancelKind.Token"/>: the local token.</summary>
    public CancellationToken Token { get; }

    /// <summary>Canceled by the language host <paramref name="milliseconds"/> after it sends the invocation.</summary>
    public static RemoteCancel After(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        return new RemoteCancel(RemoteCancelKind.After, milliseconds, CancellationToken.None);
    }

    /// <summary>Canceled by the language host <paramref name="delay"/> after it sends the invocation.</summary>
    public static RemoteCancel After(TimeSpan delay) => After(checked((int)delay.TotalMilliseconds));

    /// <summary>Canceled when <paramref name="token"/> is; <see cref="None"/> for a token that can never be canceled.</summary>
    public static RemoteCancel FromToken(CancellationToken token)
        => token.CanBeCanceled ? new RemoteCancel(RemoteCancelKind.Token, 0, token) : None;

    /// <summary>Same as <see cref="FromToken"/>.</summary>
    public static implicit operator RemoteCancel(CancellationToken token) => FromToken(token);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        RemoteCancelKind.After => "After(" + AfterMilliseconds + "ms)",
        RemoteCancelKind.Token => "Token",
        _ => "None",
    };
}
