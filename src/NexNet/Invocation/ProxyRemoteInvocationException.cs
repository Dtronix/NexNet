using System;
using System.Text;

namespace NexNet.Invocation;

/// <summary>
/// Exception thrown when a proxy invocation fails in the remote execution process.
/// </summary>
public class ProxyRemoteInvocationException : Exception
{
    private const string DefaultMessage = "Exception occurred while executing method on proxy.";

    /// <summary>
    /// Application error code the remote side returned from its <c>OnInvocationException</c>, or null.
    /// </summary>
    public int? RemoteErrorCode { get; }

    /// <summary>
    /// Full type name of the remote exception, or null. Sent only when the remote side's
    /// <see cref="NexNet.Transports.ConfigBase.RemoteExceptionDetail"/> is
    /// <see cref="NexNet.Transports.RemoteExceptionDetail.TypeAndMessage"/>.
    /// </summary>
    public string? RemoteExceptionType { get; }

    /// <summary>
    /// Message of the remote exception, or of the application error the remote side returned, or null.
    /// </summary>
    public string? RemoteMessage { get; }

    /// <summary>
    /// Constructs the exception with a default result
    /// </summary>
    public ProxyRemoteInvocationException()
        : base(DefaultMessage)
    {

    }

    /// <summary>
    /// Constructs the exception with the detail the remote side sent.
    /// </summary>
    /// <param name="remoteErrorCode">Application error code, or null.</param>
    /// <param name="remoteExceptionType">Full type name of the remote exception, or null.</param>
    /// <param name="remoteMessage">Remote message, or null.</param>
    public ProxyRemoteInvocationException(int? remoteErrorCode, string? remoteExceptionType, string? remoteMessage)
        : base(FormatMessage(remoteErrorCode, remoteExceptionType, remoteMessage))
    {
        RemoteErrorCode = remoteErrorCode;
        RemoteExceptionType = remoteExceptionType;
        RemoteMessage = remoteMessage;
    }

    /// <summary>
    /// Constructs the exception with a custom message.
    /// </summary>
    /// <param name="message">The error message.</param>
    protected ProxyRemoteInvocationException(string message)
        : base(message)
    {
    }

    private static string FormatMessage(int? code, string? type, string? message)
    {
        var builder = new StringBuilder(DefaultMessage);
        if (message != null)
        {
            builder.Append(' ');
            if (type != null)
                builder.Append(type).Append(": ");
            builder.Append(message);
        }

        if (code != null)
            builder.Append(" (code ").Append(code.Value).Append(')');

        return builder.ToString();
    }
}
