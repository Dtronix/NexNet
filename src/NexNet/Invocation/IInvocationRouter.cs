using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;

namespace NexNet.Invocation;

/// <summary>
/// Routes invocation messages to target sessions.
/// Handles all invocation modes: All, Others, Client, Clients, Group, etc.
/// Invocation ID assignment is handled internally per-session.
/// </summary>
internal interface IInvocationRouter
{
    /// <summary>
    /// Invokes on all connected sessions.
    /// </summary>
    /// <param name="message">The message to send.</param>
    ValueTask InvokeAllAsync<TMessage>(TMessage message)
        where TMessage : IInvocationMessage, IMessageBase;

    /// <summary>
    /// Invokes on all connected sessions except the specified one.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="excludeSessionId">Session ID to exclude.</param>
    ValueTask InvokeAllExceptAsync<TMessage>(TMessage message, long excludeSessionId)
        where TMessage : IInvocationMessage, IMessageBase;

    /// <summary>
    /// Invokes on a specific session by ID.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="sessionId">Target session ID.</param>
    /// <returns>True if session was found and message sent, false otherwise.</returns>
    ValueTask<bool> InvokeClientAsync<TMessage>(TMessage message, long sessionId)
        where TMessage : IInvocationMessage, IMessageBase;

    /// <summary>
    /// Invokes on multiple specific sessions by ID.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="sessionIds">Target session IDs.</param>
    ValueTask InvokeClientsAsync<TMessage>(TMessage message, long[] sessionIds)
        where TMessage : IInvocationMessage, IMessageBase;

    /// <summary>
    /// Invokes on all sessions in a group.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="groupName">Target group name.</param>
    /// <param name="excludeSessionId">Optional session ID to exclude (for GroupExceptCaller).</param>
    ValueTask InvokeGroupAsync<TMessage>(TMessage message, string groupName, long? excludeSessionId = null)
        where TMessage : IInvocationMessage, IMessageBase;

    /// <summary>
    /// Invokes on all sessions in multiple groups.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="groupNames">Target group names.</param>
    /// <param name="excludeSessionId">Optional session ID to exclude.</param>
    ValueTask InvokeGroupsAsync<TMessage>(TMessage message, string[] groupNames, long? excludeSessionId = null)
        where TMessage : IInvocationMessage, IMessageBase;

    /// <summary>
    /// Initializes the router. Called when the server starts.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the router. Called when the server stops.
    /// </summary>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
