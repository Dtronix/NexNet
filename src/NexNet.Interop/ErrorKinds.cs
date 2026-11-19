using NexNet.Invocation;
using NexNet.Serialization;
using NexNet.Transports;

namespace NexNet.Interop;

/// <summary>
/// An error reported by a client under test, in the language-neutral form of the control protocol: an
/// <see cref="ErrorKind"/> plus what the kind carries.
/// </summary>
/// <param name="Kind">The error kind.</param>
/// <param name="Message">The error message.</param>
public sealed record RemoteErrorInfo(ErrorKind Kind, string Message)
{
    /// <summary><see cref="ErrorKind.RemoteInvocation"/>: the remote exception type name, when the server sent it.</summary>
    public string? Type { get; init; }

    /// <summary><see cref="ErrorKind.RemoteInvocation"/>: the remote error code, when the server sent one.</summary>
    public int? Code { get; init; }

    /// <summary><see cref="ErrorKind.Transport"/>: the <see cref="TransportError"/> name.</summary>
    public string? Reason { get; init; }

    /// <summary><see cref="ErrorKind.TooLarge"/>: the size of the value in bytes.</summary>
    public long? Size { get; init; }

    /// <summary><see cref="ErrorKind.TooLarge"/>: the largest size allowed in bytes.</summary>
    public long? Limit { get; init; }

    /// <summary>The C# exception of this error (<see cref="ErrorKinds.ToException(RemoteErrorInfo)"/>).</summary>
    public Exception ToException() => ErrorKinds.ToException(this);
}

/// <summary>
/// Maps <see cref="ErrorKind"/> to the C# exceptions the C# client throws for the same failure, and back. A remote
/// driver rethrows host errors with <see cref="ToException(RemoteErrorInfo)"/>, so tests assert the same exact exception types on every
/// row.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><see cref="ErrorKind.Transport"/></term><description><see cref="TransportException"/></description></item>
/// <item><term><see cref="ErrorKind.RemoteInvocation"/></term><description><see cref="ProxyRemoteInvocationException"/></description></item>
/// <item><term><see cref="ErrorKind.Unauthorized"/></term><description><see cref="ProxyUnauthorizedException"/></description></item>
/// <item><term><see cref="ErrorKind.Canceled"/></term><description><see cref="TaskCanceledException"/> (D17)</description></item>
/// <item><term><see cref="ErrorKind.Disconnected"/></term><description><see cref="InvalidOperationException"/> "Session is not connected"</description></item>
/// <item><term><see cref="ErrorKind.Serialization"/></term><description><see cref="NexusSerializationException"/></description></item>
/// <item><term><see cref="ErrorKind.Busy"/></term><description><see cref="NexusBusyException"/></description></item>
/// <item><term><see cref="ErrorKind.TooLarge"/></term><description><see cref="NexusMessageTooLargeException"/></description></item>
/// <item><term><see cref="ErrorKind.InvalidOperation"/></term><description><see cref="InvalidOperationException"/></description></item>
/// </list>
/// </remarks>
public static class ErrorKinds
{
    /// <summary>The message of the C# client when it invokes on a disconnected session.</summary>
    public const string NotConnectedMessage = "Session is not connected";

    /// <summary>The C# exception of <paramref name="error"/>.</summary>
    public static Exception ToException(RemoteErrorInfo error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Kind switch
        {
            ErrorKind.Transport => new TransportException(
                Enum.TryParse<TransportError>(error.Reason, ignoreCase: true, out var reason) ? reason : TransportError.InternalError,
                error.Message,
                (Exception?)null),
            ErrorKind.RemoteInvocation => new ProxyRemoteInvocationException(error.Code, error.Type, error.Message),
            ErrorKind.Unauthorized => new ProxyUnauthorizedException(),
            ErrorKind.Canceled => new TaskCanceledException(error.Message),
            // The C# client's own message, so assertions on it hold on every row.
            ErrorKind.Disconnected => new InvalidOperationException(NotConnectedMessage),
            ErrorKind.Serialization => new NexusSerializationException(error.Message),
            ErrorKind.Busy => new NexusBusyException(error.Message),
            ErrorKind.TooLarge => new NexusMessageTooLargeException(error.Size ?? 0, error.Limit ?? 0),
            ErrorKind.InvalidOperation => new InvalidOperationException(error.Message),
            _ => throw new ArgumentOutOfRangeException(nameof(error), error.Kind, "Unknown error kind."),
        };
    }

    /// <summary>The C# exception of an error of <paramref name="kind"/> with <paramref name="message"/>.</summary>
    public static Exception ToException(ErrorKind kind, string message) => ToException(new RemoteErrorInfo(kind, message));

    /// <summary>The language-neutral form of a C# exception.</summary>
    public static RemoteErrorInfo FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
            exception = aggregate.InnerExceptions[0];

        return exception switch
        {
            TransportException t => new RemoteErrorInfo(ErrorKind.Transport, t.Message) { Reason = t.Error.ToString() },
            ProxyUnauthorizedException u => new RemoteErrorInfo(ErrorKind.Unauthorized, u.Message),
            ProxyRemoteInvocationException r => new RemoteErrorInfo(ErrorKind.RemoteInvocation, r.RemoteMessage ?? r.Message)
            {
                Type = r.RemoteExceptionType,
                Code = r.RemoteErrorCode,
            },
            OperationCanceledException c => new RemoteErrorInfo(ErrorKind.Canceled, c.Message),
            NexusSerializationException s => new RemoteErrorInfo(ErrorKind.Serialization, s.Message),
            NexusBusyException b => new RemoteErrorInfo(ErrorKind.Busy, b.Message),
            NexusMessageTooLargeException l => new RemoteErrorInfo(ErrorKind.TooLarge, l.Message) { Size = l.Size, Limit = l.Limit },
            InvalidOperationException i when i.Message == NotConnectedMessage => new RemoteErrorInfo(ErrorKind.Disconnected, i.Message),
            InvalidOperationException i => new RemoteErrorInfo(ErrorKind.InvalidOperation, i.Message),
            _ => new RemoteErrorInfo(ErrorKind.InvalidOperation, exception.Message) { Type = exception.GetType().FullName },
        };
    }
}
