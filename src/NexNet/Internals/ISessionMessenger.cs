using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;

namespace NexNet.Internals;

/// <summary>
/// Base interface for sending messages and disconnecting.
/// </summary>
internal interface ISessionMessenger
{
    /// <summary>
    /// Sends a message.
    /// </summary>
    /// <typeparam name="TMessage">Type of message to send. Must implement IMessageBodyBase</typeparam>
    /// <param name="body">Message to send.</param>
    /// <param name="cancellationToken">Cancellation token to cancel sending.</param>
    /// <returns>Task which completes upon sending.</returns>
    ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default)
        where TMessage : IMessageBase;

    /// <summary>
    /// Sends the passed sequence with prefixed header type and length.
    /// </summary>
    /// <param name="type">Type of header to send.</param>
    /// <param name="body">Sequence of data to send in teh body</param>
    /// <param name="cancellationToken">Cancellation token for sending.</param>
    /// <returns>Task which completes upon sending.</returns>
    ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a header over the wire.
    /// </summary>
    /// <param name="type">Type of header to send.</param>
    /// <param name="cancellationToken">Cancellation token to cancel sending.</param>
    /// <returns>Task which completes upon sending.</returns>
    ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a header with a given body over the wire.
    /// </summary>
    /// <param name="type">Type of header to send.</param>
    /// <param name="messageHeader">The header message to be sent</param>
    /// <param name="body">Sequence of data to send in the body</param>
    /// <param name="cancellationToken">Cancellation token for sending.</param>
    /// <returns>Task which completes upon sending.</returns>
    ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects the session with the specified reason.
    /// </summary>
    /// <param name="reason">The reason for disconnection.</param>
    /// <param name="filePath">Source file path where disconnect was initiated.</param>
    /// <param name="lineNumber">Source line number where disconnect was initiated.</param>
    /// <returns>Task which completes upon disconnection.</returns>
    Task DisconnectAsync(DisconnectReason reason ,
        [CallerFilePath]string? filePath = null,
        [CallerLineNumber] int? lineNumber = null);
}
