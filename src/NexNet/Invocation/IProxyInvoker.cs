using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.Invocation;

/// <summary>
/// Interface for invocations on remote hubs.
/// </summary>
public interface IProxyInvoker
{
    /// <summary>
    /// Configures the proxy invoker with the specified parameters.
    /// </summary>
    /// <param name="session">The session to be used by the proxy invoker. Null on the server.</param>
    /// <param name="sessionManager">The session manager to be used by the proxy invoker. Null on the client</param>
    /// <param name="mode">The invocation mode to be used by the proxy invoker.</param>
    /// <param name="modeArguments">The arguments for the invocation mode. Can be null.</param>
    internal void Configure(
        INexusSession? session,
        SessionManager? sessionManager,
        ProxyInvocationMode mode,
        object? modeArguments);
    
    /// <summary>
    /// Logger for the current session.
    /// </summary>
    INexusLogger? Logger { get; }

    /// <summary>
    /// Invokes the specified method on the connected session and waits until the message has been completely sent.
    /// Will not wait for results on invocations and will instruct the proxy to dismiss any results.
    /// </summary>
    /// <param name="methodId">Method ID to invoke.</param>
    /// <param name="arguments">Optional arguments to pass to the method invocation.</param>
    /// <param name="flags">Special flags for the invocation of this method.</param>
    /// <returns>Task which returns when the invocations messages have been issued.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the invocation mode is set in an invalid mode.</exception>
    ValueTask ProxyInvokeMethodCore(ushort methodId, ITuple? arguments, InvocationFlags flags);

    /// <summary>
    /// Invokes a method ID on the connection with the optionally passed arguments and optional cancellation token
    /// and waits the completion of the invocation.
    /// </summary>
    /// <param name="methodId">Method ID to invoke.</param>
    /// <param name="arguments">Optional arguments to pass to the method invocation</param>
    /// <param name="cancellationToken">Optional cancellation token to allow cancellation of remote invocation.</param>
    /// <returns>ValueTask which completes upon remote invocation completion.</returns>
    /// <exception cref="ProxyRemoteInvocationException">Throws this exception if the remote invocation threw an exception.</exception>
    /// <exception cref="InvalidOperationException">Invocation returned invalid state data upon completion.</exception>
    ValueTask ProxyInvokeAndWaitForResultCore(ushort methodId, ITuple? arguments, CancellationToken? cancellationToken = null);

    /// <summary>
    /// Invokes a method ID on the connection with the optionally passed arguments and optional cancellation token,
    /// waits the completion of the invocation and returns the value of the invocation.
    /// </summary>
    /// <typeparam name="TReturn">Expected type to be returned by the remote invocation proxy.</typeparam>
    /// <param name="methodId">Method ID to invoke.</param>
    /// <param name="arguments">Optional arguments to pass to the method invocation</param>
    /// <param name="cancellationToken">Optional cancellation token to allow cancellation of remote invocation.</param>
    /// <returns>ValueTask with the containing return result which completes upon remote invocation completion.</returns>
    /// <exception cref="ProxyRemoteInvocationException">Throws this exception if the remote invocation threw an exception.</exception>
    /// <exception cref="InvalidOperationException">Invocation returned invalid state data upon completion.</exception>
    ValueTask<TReturn> ProxyInvokeAndWaitForResultCore<TReturn>(ushort methodId, ITuple? arguments, CancellationToken? cancellationToken = null);

    /// <summary>
    /// Gets the Initial Id of the duplex pipe.
    /// </summary>
    /// <param name="pipe">Pipe to retrieve the Id of.</param>
    /// <returns>Initial id of the pipe.</returns>
    byte ProxyGetDuplexPipeInitialId(INexusDuplexPipe? pipe);


    /// <summary>
    /// Provides client side access to a configured list. 
    /// </summary>
    /// <param name="id">
    ///     A unique identifier for the list collection to configure.  
    ///     This must match the identifier used when retrieving or starting the collection.
    /// </param>
    /// <typeparam name="T">The element type stored in the list.</typeparam>
    /// <returns>List ready for use.</returns>
    INexusList<T> ProxyGetConfiguredNexusList<T>(ushort id);

}
