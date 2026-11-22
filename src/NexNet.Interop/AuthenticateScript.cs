namespace NexNet.Interop;

/// <summary>
/// What the scripted authenticate callback of a client under test does (<see cref="ClientSpec.Authenticate"/>): each
/// call counts, waits <see cref="DelayMs"/>, then hangs, throws or returns the token, in that order of precedence.
/// </summary>
/// <remarks>Delays run in real time, also with <see cref="ClientSpec.FakeTime"/>.</remarks>
public sealed class AuthenticateScript
{
    /// <summary>The token, as UTF-8 text; null returns no token.</summary>
    public string? Token { get; set; }

    /// <summary>Appends the number of the call (1 for the first call of the client, 2 for the next, ...) to <see cref="Token"/>.</summary>
    public bool AppendCallNumber { get; set; }

    /// <summary>Milliseconds the callback waits, asynchronously, before it answers.</summary>
    public int DelayMs { get; set; }

    /// <summary>Never answers: only the client's own cancellation (handshake timeout, canceled connect) ends the call.</summary>
    public bool Hang { get; set; }

    /// <summary>Fails the call with an error of this message.</summary>
    public string? ThrowMessage { get; set; }

    /// <summary>A copy.</summary>
    public AuthenticateScript Clone() => (AuthenticateScript)MemberwiseClone();
}
