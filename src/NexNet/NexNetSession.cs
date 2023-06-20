using System.Buffers;
using System.Collections.Generic;
using NexNet.Messages;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Transports;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Collections.Concurrent;
using Pipelines.Sockets.Unofficial.Threading;
using MemoryPack;
using Pipelines.Sockets.Unofficial.Buffers;
using System.Runtime.CompilerServices;

namespace NexNet;

internal class NexNetSession<THub, TProxy> : INexNetSession<TProxy>
    where THub : HubBase<TProxy>, IMethodInvoker<TProxy>, IInvocationMethodHash
    where TProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private readonly THub _hub;
    private readonly ConfigBase _config;
    private readonly SessionCacheManager<TProxy> _cacheManager;
    private readonly SessionManager? _sessionManager;

    private ITransport _transportConnection;
    private PipeReader? _pipeInput;
    private PipeWriter? _pipeOutput;

    private readonly MutexSlim _writeMutex = new MutexSlim(Int32.MaxValue);
    private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 8);
    private readonly byte[] _bodyLengthBuffer = new byte[2];

    // mutable struct.  Don't set to readonly.
    private MessageHeader _recMessageHeader = new MessageHeader();

    private readonly ConcurrentBag<InvocationTaskArguments> _invocationTaskArgumentsPool = new();

    private readonly SemaphoreSlim _invocationSemaphore;
    private readonly bool _isServer;
    private bool _isReconnected;
    private DisconnectReason _registeredDisconnectReason = DisconnectReason.None;

    private readonly TaskCompletionSource _readyTaskCompletionSource = new TaskCompletionSource();
    private readonly TaskCompletionSource _disconnectedTaskCompletionSource = new TaskCompletionSource();

    public long Id { get; }

    public SessionManager? SessionManager => _sessionManager;
    public SessionInvocationStateManager SessionInvocationStateManager { get; }
    public long LastReceived { get; private set; }

    public List<int> RegisteredGroups { get; } = new List<int>();

    public SessionCacheManager<TProxy> CacheManager => _cacheManager;
    public SessionStore SessionStore { get; }

    public Action? OnDisconnected { get; set; }

    public IIdentity? Identity { get; private set; }

    public Action? OnSent { get; set; }

    public ConnectionState State { get; private set; }

    public Task ReadyTask => _readyTaskCompletionSource.Task;

    public Task DisconnectedTask => _disconnectedTaskCompletionSource.Task;
    
    public NexNetSession(in NexNetSessionConfigurations<THub, TProxy> configurations)
    {
        State = ConnectionState.Connecting;
        Id = configurations.Id;
        _pipeInput = configurations.Transport.Input;
        _pipeOutput = configurations.Transport.Output;
        _transportConnection = configurations.Transport;
        _config = configurations.Configs;
        _cacheManager = configurations.Cache;
        _sessionManager = configurations.SessionManager;
        _isServer = configurations.IsServer;
        _hub = configurations.Hub;
        _hub.SessionContext = configurations.IsServer
            ? new ServerSessionContext<TProxy>(this, _sessionManager!)
            : new ClientSessionContext<TProxy>(this);

        SessionInvocationStateManager = new SessionInvocationStateManager(configurations.Cache);
        SessionStore = new SessionStore();
        _invocationSemaphore = new SemaphoreSlim(configurations.Configs.MaxConcurrentConnectionInvocations,
            configurations.Configs.MaxConcurrentConnectionInvocations);

        // Register the session if there is a manager.
        configurations.SessionManager?.RegisterSession(this);

        _config.InternalOnSessionSetup?.Invoke(this);
    }

    public async ValueTask SendHeaderWithBody<TMessage>(TMessage body, CancellationToken cancellationToken = default)
        where TMessage : IMessageBodyBase
    {
        if (_pipeOutput == null || cancellationToken.IsCancellationRequested)
            return;

        if (State != ConnectionState.Connected && State != ConnectionState.Disconnecting)
            return;

        using var mutexResult = await _writeMutex.TryWaitAsync(cancellationToken).ConfigureAwait(false);

        var header = _bufferWriter.GetMemory(3);
        _bufferWriter.Advance(3);
        MemoryPackSerializer.Serialize(_bufferWriter, body);

        var contentLength = checked((ushort)(_bufferWriter.Length - 3));
        
        header.Span[0] = (byte)TMessage.Type;

        BitConverter.TryWriteBytes(header.Span.Slice(1, 2), contentLength);

        var length = (int)_bufferWriter.Length;

        using var buffer = _bufferWriter.Flush();

        _config.InternalOnSend?.Invoke(this, buffer.Value.ToArray());

        buffer.Value.CopyTo(_pipeOutput.GetSpan(length));
        _pipeOutput.Advance(length);

        _config.Logger?.LogTrace($"Sending {TMessage.Type} message & body with {length} bytes.");

        var result = await _pipeOutput.FlushAsync(cancellationToken).ConfigureAwait(false);

        OnSent?.Invoke();

        if (result.IsCanceled || result.IsCompleted)
            await DisconnectCore(DisconnectReason.ProtocolError, false).ConfigureAwait(false);
    }

    public async ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default)
    {
        if (_pipeOutput == null || cancellationToken.IsCancellationRequested)
            return;

        if (State != ConnectionState.Connected && State != ConnectionState.Disconnecting)
            return;

        using var mutexResult = await _writeMutex.TryWaitAsync(cancellationToken).ConfigureAwait(false);

        if (mutexResult.Success != true)
            throw new InvalidOperationException("Could not acquire write lock");

        _config.InternalOnSend?.Invoke(this, new[] { (byte)type });

        _pipeOutput.GetSpan(1)[0] = (byte)type;
        _pipeOutput.Advance(1);

        _config.Logger?.LogTrace($"Sending {type} header.");
        FlushResult result = default;
        try
        {
            result = await _pipeOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {

        }

        OnSent?.Invoke();

        if (result.IsCanceled || result.IsCompleted)
            await DisconnectCore(DisconnectReason.ProtocolError, false).ConfigureAwait(false);
    }

    public Task DisconnectAsync(DisconnectReason reason)
    {
        return DisconnectCore(reason, true);
    }


    public bool DisconnectIfTimeout(long timeoutTicks)
    {
        if (State != ConnectionState.Connected)
            return false;

        if (timeoutTicks > LastReceived)
        {
            _config.Logger?.LogTrace($"Timed out session {Id}");
            DisconnectAsync(DisconnectReason.Timeout);
            return true;
        }

        return false;
    }


    public async Task StartReadAsync()
    {
        _config.Logger?.LogTrace($"NexNetSession.StartReadAsync()");
        State = ConnectionState.Connected;
        try
        {
            while (true)
            {
                if (_pipeInput == null
                    || State != ConnectionState.Connected)
                    return;

                var result = await _pipeInput.ReadAsync().ConfigureAwait(false);

                LastReceived = Environment.TickCount64;

                var (position, disconnect, issueDisconnectMessage) = await Process(result.Buffer).ConfigureAwait(false);

                _config.Logger?.LogTrace($"Reading completed.");

                if (disconnect != DisconnectReason.None)
                {
                    await DisconnectCore(disconnect, issueDisconnectMessage).ConfigureAwait(false);
                    return;
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    _config.Logger?.LogTrace($"Reading completed with IsCompleted: {result.IsCompleted} and IsCanceled: {result.IsCanceled}.");

                    if (_registeredDisconnectReason == DisconnectReason.None)
                    {
                        _config.Logger?.LogTrace("Disconnected without a reason.");

                        // If there is not a disconnect reason, then we disconnected for an unknown reason and should 
                        // be allowed to reconnect.
                        await DisconnectCore(DisconnectReason.SocketError, false).ConfigureAwait(false);
                    }
                    return;
                }

                _pipeInput?.AdvanceTo(position);
            }
        }
        catch (NullReferenceException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _config.Logger?.LogError(ex, "Reading exited with exception.");
            await DisconnectCore(DisconnectReason.SocketError, false).ConfigureAwait(false);
        }
    }

    private async ValueTask<(SequencePosition, DisconnectReason, bool)> Process(ReadOnlySequence<byte> sequence)
    {
        var position = 0;
        var maxLength = sequence.Length;
        DisconnectReason disconnect = DisconnectReason.None;
        var issueDisconnectMessage = true;
        while (true)
        {
            if (_recMessageHeader.Type == MessageType.Unset || _recMessageHeader.BodyLength == 0)
            {
                if (_recMessageHeader.BodyLength == ushort.MaxValue)
                {
                    if (position >= maxLength)
                    {
                        _config.Logger?.LogTrace($"Could not read next header type. No more data.");
                        break;
                    }

                    var type = _recMessageHeader.Type = (MessageType)sequence.Slice(position, 1).FirstSpan[0];
                    position++;
                    _config.Logger?.LogTrace($"Received {type} header.");

                    // Header only messages
                    if ((byte)type >= 20 && (byte)type < 40)
                    {
                        _config.Logger?.LogTrace($"Received disconnect message.");
                        // Translate the type over to the reason.
                        disconnect = (DisconnectReason)_recMessageHeader.Type;
                        issueDisconnectMessage = false;
                        break;
                    }
                    else if (type == MessageType.Ping)
                    {
                        _recMessageHeader.Reset();

                        // If we are the server, send back a ping message to help the client know if it is still connected.
                        if (_isServer)
                            await SendHeader(MessageType.Ping).ConfigureAwait(false);

                        continue;
                    }
                    else
                    {
                        _config.Logger?.LogTrace($"Message has a body.");
                        _recMessageHeader.BodyLength = 0;
                    }
                }

                if (_recMessageHeader.BodyLength == 0)
                {
                    // Read the body length size.

                    if (position + 2 > maxLength)
                    {
                        _config.Logger?.LogTrace($"Could not read the next two bytes for the body length. Not enough data.");
                        break;
                    }

                    try
                    {
                        var lengthSlice = sequence.Slice(position, 2);
                        position += 2;
                        // If this is a single segment, we can just treat it like a single span.
                        // If we cross multiple spans, we need to copy the memory into a single
                        // continuous span.
                        if (lengthSlice.IsSingleSegment)
                        {
                            _recMessageHeader.BodyLength = BitConverter.ToUInt16(lengthSlice.FirstSpan);
                        }
                        else
                        {
                            lengthSlice.CopyTo(_bodyLengthBuffer);
                            _recMessageHeader.BodyLength = BitConverter.ToUInt16(_bodyLengthBuffer);
                        }

                        _config.Logger?.LogTrace($"Parsed body length of {_recMessageHeader.BodyLength}.");
                        // ReSharper disable once RedundantJumpStatement
                        continue;
                    }
                    catch (Exception e)
                    {
                        _config.Logger?.LogTrace($"Reset data due to transport error. {e}");
                        //_logger?.LogError(e, $"Could not parse message header with {bodyLength.Length} bytes.");
                        disconnect = DisconnectReason.ProtocolError;
                        break;
                    }
                }
            }
            else
            {
                // Read the body.
                if (position + _recMessageHeader.BodyLength > maxLength)
                {
                    _config.Logger?.LogTrace($"Could not read all the {_recMessageHeader.BodyLength} body bytes.");
                    break;
                }
                _config.Logger?.LogTrace($"Read all the {_recMessageHeader.BodyLength} body bytes.");

                var bodySlice = sequence.Slice(position, _recMessageHeader.BodyLength);

                position += _recMessageHeader.BodyLength;
                try
                {
                    IMessageBodyBase? body = null;
                    switch (_recMessageHeader.Type)
                    {
                        case MessageType.GreetingClient:
                            body = _cacheManager.ClientGreetingDeserializer.Deserialize(bodySlice);
                            break;

                        case MessageType.GreetingServer:
                            body = _cacheManager.ServerGreetingDeserializer.Deserialize(bodySlice);
                            break;

                        case MessageType.InvocationWithResponseRequest:
                            body = _cacheManager.InvocationRequestDeserializer.Deserialize(bodySlice);
                            break;

                        case MessageType.InvocationProxyResult:
                            body = _cacheManager.InvocationProxyResultDeserializer.Deserialize(bodySlice);
                            break;

                        case MessageType.InvocationCancellationRequest:
                            body = _cacheManager.InvocationCancellationRequestDeserializer.Deserialize(bodySlice);
                            break;

                        default:
                            _config.Logger?.LogError($"Deserialized type not recognized. {_recMessageHeader.Type}.");
                            disconnect = DisconnectReason.ProtocolError;
                            break;
                    }

                    // Used only when the header type is not recognized.
                    if (disconnect != DisconnectReason.None)
                        break;

                    _config.Logger?.LogTrace($"Handling {_recMessageHeader.Type} message.");
                    disconnect = await MessageHandler(body!).ConfigureAwait(false);

                    if (disconnect != DisconnectReason.None)
                    {
                        _config.Logger?.LogTrace($"Message could not be handled and disconnected with {disconnect}");
                        break;
                    }
                    _config.Logger?.LogTrace($"Resetting header.");
                    // Reset the header.
                    _recMessageHeader.Reset();
                }
                catch (Exception e)
                {
                    _config.Logger?.LogTrace(e, "Could not deserialize body.");
                    //_logger?.LogError(e, $"Could not deserialize body..");
                    disconnect = DisconnectReason.ProtocolError;
                    break;
                }

                _recMessageHeader.Reset();
            }

        }

        var seqPosition = sequence.GetPosition(position);
        return (seqPosition, disconnect, issueDisconnectMessage);
    }


    private async ValueTask<DisconnectReason> MessageHandler(IMessageBodyBase message)
    {
        static async void InvokeOnConnected(object? sessionObj)
        {
            var session = Unsafe.As<NexNetSession<THub, TProxy>>(sessionObj)!;
            await session._hub.Connected(session._isReconnected).ConfigureAwait(false);

            // Reset the value;
            session._isReconnected = false;
        }

        if (message is ClientGreetingMessage cGreeting)
        {
            // Verify that this is the server
            if (!_isServer)
                return DisconnectReason.ProtocolError;

            // Verify what the greeting method hashes matches this hub's and proxy's
            if (cGreeting.ServerHubMethodHash != THub.MethodHash)
            {
                return DisconnectReason.ServerHubMismatch;
            }

            if (cGreeting.ClientHubMethodHash != TProxy.MethodHash)
            {
                return DisconnectReason.ClientHubMismatch;
            }

            var serverConfig = Unsafe.As<ServerConfig>(_config);

            // See if there is an authentication handler.
            if (serverConfig.Authenticate)
            {
                // Run the handler and verify that it is good.
                var serverHub = Unsafe.As<ServerHubBase<TProxy>>(_hub);
                Identity = await serverHub.Authenticate(cGreeting.AuthenticationToken);

                // Set the identity on the context.
                var serverContext = Unsafe.As<ServerSessionContext<TProxy>>(_hub.SessionContext);
                serverContext.Identity = Identity;

                // If the token is not good, disconnect.
                if (Identity == null)
                    return DisconnectReason.Authentication;
            }

            var serverGreeting = _cacheManager.ServerGreetingDeserializer.Rent();
            serverGreeting.Version = 0;

            await SendHeaderWithBody(serverGreeting).ConfigureAwait(false);

            _cacheManager.ServerGreetingDeserializer.Return(serverGreeting);
            _sessionManager!.RegisterSession(this);

            _ = Task.Factory.StartNew(InvokeOnConnected, this);

            _readyTaskCompletionSource.TrySetResult();
        }
        else if (message is ServerGreetingMessage)
        {
            // Verify that this is the client
            if (_isServer)
                return DisconnectReason.ProtocolError;

            _ = Task.Factory.StartNew(InvokeOnConnected, this);

            _readyTaskCompletionSource.TrySetResult();
        }
        else if (message is InvocationRequestMessage invocationRequestMessage)
        {
            // Throttle invocations.
            await _invocationSemaphore.WaitAsync().ConfigureAwait(false);

            if (!_invocationTaskArgumentsPool.TryTake(out var args))
                args = new InvocationTaskArguments();

            args.Message = invocationRequestMessage;
            args.Session = this;

            static async void InvocationTask(object? argumentsObj)
            {
                var arguments = Unsafe.As<InvocationTaskArguments>(argumentsObj)!;

                var session = arguments.Session;
                var message = arguments.Message;
                try
                {
                    await session._hub.InvokeMethod(message).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    session._config.Logger?.LogError(e, $"Invoked method {message.MethodId} threw exception");
                }
                finally
                {
                    session._invocationSemaphore.Release();

                    // Clear out the references before returning to the pool.
                    arguments.Session = null!;
                    arguments.Message = null!;
                    session._invocationTaskArgumentsPool.Add(arguments);
                }
            }
            _ = Task.Factory.StartNew(InvocationTask, args);

        }
        else if (message is InvocationProxyResultMessage invocationProxyResultMessage)
        {
            SessionInvocationStateManager.UpdateInvocationResult(invocationProxyResultMessage);
        }
        else if (message is InvocationCancellationRequestMessage invocationCancellationRequestMessage)
        {
            _hub.CancelInvocation(invocationCancellationRequestMessage);
        }
        else
        {
            // If we don't know what the message is, then disconnect the connection
            // as we have received invalid/unexpected data.
            return DisconnectReason.ProtocolError;
        }

        return DisconnectReason.None;
    }

    public async ValueTask StartAsClient()
    {
        _config.Logger?.LogTrace("NexNetSession.StartAsClient()");
        var clientConfig = Unsafe.As<ClientConfig>(_config);
        var greetingMessage = _cacheManager.ClientGreetingDeserializer.Rent();

        greetingMessage.Version = 0;
        greetingMessage.ServerHubMethodHash = TProxy.MethodHash;
        greetingMessage.ClientHubMethodHash = THub.MethodHash;
        greetingMessage.AuthenticationToken = clientConfig.Authenticate?.Invoke();

        State = ConnectionState.Connected;

        await SendHeaderWithBody(greetingMessage).ConfigureAwait(false);

        _cacheManager.ClientGreetingDeserializer.Return(greetingMessage);

        // ReSharper disable once MethodSupportsCancellation
        _ = Task.Factory.StartNew(StartReadAsync, TaskCreationOptions.LongRunning);
    }

    private async ValueTask<bool> TryReconnectAsClient()
    {
        _config.Logger?.LogTrace("NexNetSession.TryReconnectAsClient()");
        if (_isServer)
            return false;

        var clientConfig = Unsafe.As<ClientConfig>(_config);
        var clientHub = Unsafe.As<ClientHubBase<TProxy>>(_hub);

        if (clientConfig.ReconnectionPolicy == null)
            return false;

        State = ConnectionState.Reconnecting;

        // Notify the hub.
        await clientHub.Reconnecting().ConfigureAwait(false);
        int count = 0;

        while (true)
        {
            ITransport? transport = null;
            try
            {
                // Get the next delay or cancellation.
                var delay = clientConfig.ReconnectionPolicy.ReconnectDelay(count++);

                if (delay == null)
                    return false;

                await Task.Delay(delay.Value).ConfigureAwait(false);

                _config.Logger?.LogTrace($"Reconnection attempt {count}");

                transport = await clientConfig.ConnectTransport().ConfigureAwait(false);
                State = ConnectionState.Connecting;

                _pipeInput = transport.Input;
                _pipeOutput = transport.Output;
                _transportConnection = transport;

                _config.Logger?.LogTrace($"Reconnection succeeded.");

                _isReconnected = true;
                await StartAsClient().ConfigureAwait(false);
                return true;
            }
            catch (Exception e)
            {
                _config.Logger?.LogError(e, "Reconnection failed with exception.");
                transport?.Close(true);
            }
        }

    }

    private async Task DisconnectCore(DisconnectReason reason, bool sendDisconnect)
    {
        // If we are already disconnecting, don't do anything
        if (State == ConnectionState.Disconnecting || State == ConnectionState.Disconnecting)
            return;

        State = ConnectionState.Disconnecting;

        _registeredDisconnectReason = reason;

        _config.Logger?.LogTrace($"NexNetSession.DisconnectCore({reason}, {sendDisconnect})");

        if (sendDisconnect && !_config.InternalForceDisableSendingDisconnectSignal)
        {
            await SendHeader((MessageType)reason).ConfigureAwait(false);

            if (_config.DisconnectDelay > 0)
            {
                // Add a delay in here to ensure that the data has a chance to send on the wire before a full disconnection.
                await Task.Delay(_config.DisconnectDelay).ConfigureAwait(false);
            }
        }

        // This can not be stopped on some transports as they don't have an understanding about
        // shutting down of rending pipes separately from receiving pipes.
        // ReSharper disable once MethodHasAsyncOverload
        _pipeInput!.Complete();
        _pipeInput = null;

        if (_config.InternalNoLingerOnShutdown)
        {
            _transportConnection.Close(false);
            return;
        }
        else
        {
            // Cancel all current invocations.
            SessionInvocationStateManager.CancelAll();

            // ReSharper disable once MethodHasAsyncOverload
            try
            {
                _pipeOutput!.Complete();
            }
            catch (ObjectDisposedException)
            {
                //noop
            }

            _pipeOutput = null;
            _transportConnection.Close(true);
        }

        // If we match a limited type of disconnects, attempt to reconnect if we are the client
        if (_isServer == false
            && reason == DisconnectReason.SocketError
            || reason == DisconnectReason.Timeout
            || reason == DisconnectReason.ServerRestarting)
        {
            // If we reconnect, stop the disconnection process.
            if (await TryReconnectAsClient().ConfigureAwait(false))
                return;
        }

        State = ConnectionState.Disconnected;

        _hub.Disconnected(reason);
        OnDisconnected?.Invoke();

        _hub.SessionContext.Reset();

        _sessionManager?.UnregisterSession(this);
        ((IDisposable)SessionStore).Dispose();
        _invocationTaskArgumentsPool.Clear();

        // Let any waiting tasks pass.
        _disconnectedTaskCompletionSource.TrySetResult();
        _readyTaskCompletionSource.TrySetResult();
    }

    private class InvocationTaskArguments
    {
        public InvocationRequestMessage Message { get; set; } = null!;
        public NexNetSession<THub, TProxy> Session { get; set; } = null!;
    }
}
