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
using Pipelines.Sockets.Unofficial;
using System.Net.Sockets;
using System.Net;

namespace NexNet;

internal class NexNetSession<THub, TProxy> : INexNetSession<TProxy>
    where THub : HubBase<TProxy>, IMethodInvoker<TProxy>, IInterfaceMethodHash
    where TProxy : ProxyInvocationBase, IProxyInvoker, IInterfaceMethodHash, new()
{
    private readonly THub _hub;
    private readonly ConfigBase _config;
    private readonly SessionCacheManager<TProxy> _cacheManager;
    private readonly SessionManager? _sessionManager;

    private ITransportBase _transportConnection;
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
    private bool _isReconnected = false;


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
    
    public NexNetSession(in NexNetSessionConfigurations<THub, TProxy> configurations)
    {
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
            ? new ServerSessionContext<TProxy>(this)
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
        if (_pipeOutput == null)
            return;

        if (cancellationToken.IsCancellationRequested)
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
            await DisconnectCore(DisconnectReason.TransportError, false);
    }

    public async ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default)
    {
        if (_pipeOutput == null)
            return;

        if (cancellationToken.IsCancellationRequested)
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
            await DisconnectCore(DisconnectReason.TransportError, false);
    }
    public Task DisconnectAsync(DisconnectReason reason)
    {
        return DisconnectCore(reason, true);
    }

    private async Task StartReadAsync()
    {
        _config.Logger?.LogTrace($"NexNetSession.StartReadAsync()");
        try
        {
            while (true)
            {
                if (_pipeInput == null)
                    return;

                var result = await _pipeInput.ReadAsync().ConfigureAwait(false);

                LastReceived = Environment.TickCount64;

                var (position, disconnect, issueDisconnectMessage) = await Process(result.Buffer).ConfigureAwait(false);

                _config.Logger?.LogTrace($"Reading completed.");

                if (disconnect != DisconnectReason.None)
                {
                    await DisconnectCore(disconnect, issueDisconnectMessage);
                    return;
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    _config.Logger?.LogTrace($"Reading completed with IsCompleted: {result.IsCompleted} and IsCanceled: {result.IsCanceled}.");
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
            await DisconnectCore(DisconnectReason.DisconnectSocketError, false);
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
                    _config.Logger?.LogTrace($"Received {(MessageType)type} header.");

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
                        continue;
                    }
                    catch (Exception e)
                    {
                        _config.Logger?.LogTrace($"Reset data due to transport error. {e.ToString()}");
                        //_logger?.LogError(e, $"Could not parse message header with {bodyLength.Length} bytes.");
                        disconnect = DisconnectReason.TransportError;
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
                            body = _cacheManager.InvocationCancellationRequestDeserializer.Deserialize(bodySlice); ;
                            break;

                        default:
                            _config.Logger?.LogError($"Deserialized type not recognized. {_recMessageHeader.Type}.");
                            disconnect = DisconnectReason.TransportError;
                            break;
                    }

                    _config.Logger?.LogTrace($"Handling {_recMessageHeader.Type} message.");
                    disconnect = await MessageHandler(body!, _recMessageHeader.Type);
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
                    _config.Logger?.LogTrace("Could not deserialize body.");
                    //_logger?.LogError(e, $"Could not deserialize body..");
                    disconnect = DisconnectReason.TransportError;
                    break;
                }

                _recMessageHeader.Reset();
            }

        }

        var seqPosition = sequence.GetPosition(position);
        return (seqPosition, disconnect, issueDisconnectMessage);
    }


    private async ValueTask<DisconnectReason> MessageHandler(IMessageBodyBase message, MessageType messageType)
    {
        static async void InvokeOnConnected(object? sessionObj)
        {
            var session = Unsafe.As<NexNetSession<THub, TProxy>>(sessionObj)!;
            await session._hub.Connected(session._isReconnected);

            // Reset the value;
            session._isReconnected = false;
        }

        if (message is ClientGreetingMessage cGreeting)
        {
            // Verify that this is the server
            if (!_isServer)
                return DisconnectReason.TransportError;

            // Verify what the greeting method hashes matches this hub's and proxy's
            if (cGreeting.ServerHubMethodHash != THub.MethodHash)
            {
                return DisconnectReason.DisconnectServerHubMismatch;
            }

            if (cGreeting.ClientHubMethodHash != TProxy.MethodHash)
            {
                return DisconnectReason.DisconnectClientHubMismatch;
            }

            var serverConfig = Unsafe.As<ServerConfig>(_config);

            // See if there is an authentication handler.
            if (serverConfig.Authenticate)
            {
                // Run the handler and verify that it is good.
                var serverHub = Unsafe.As<ServerHubBase<TProxy>>(_hub);
                Identity = await serverHub.Authenticate(cGreeting.AuthenticationToken);

                // If the token is not good, disconnect.
                if (Identity == null)
                    return DisconnectReason.DisconnectAuthentication;
            }

            var serverGreeting = _cacheManager.ServerGreetingDeserializer.Rent();
            serverGreeting.Version = 0;

            await SendHeaderWithBody(serverGreeting);

            _cacheManager.ServerGreetingDeserializer.Return(serverGreeting);
            _sessionManager!.RegisterSession(this);

            _ = Task.Factory.StartNew(InvokeOnConnected, this);
        }
        else if (message is ServerGreetingMessage sGreeting)
        {
            // Verify that this is the client
            if (_isServer)
                return DisconnectReason.TransportError;

            _ = Task.Factory.StartNew(InvokeOnConnected, this);

        }
        else if (message is InvocationRequestMessage invocationRequestMessage)
        {
            // Throttle invocations.
            await _invocationSemaphore.WaitAsync();

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
                    await session._hub.InvokeMethod(message);
                }
                catch (Exception e)
                {
                    session._config.Logger?.LogError(e, $"Invoked method {message.MethodId} threw exception");
                }
                finally
                {
                    session._invocationSemaphore.Release();

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
            return DisconnectReason.DisconnectMessageParsingError;
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

        await SendHeaderWithBody(greetingMessage);

        _cacheManager.ClientGreetingDeserializer.Return(greetingMessage);

        // ReSharper disable once MethodSupportsCancellation

        _ = Task.Factory.StartNew(StartReadAsync, TaskCreationOptions.LongRunning);

    }

    public Task StartAsServer()
    {
        _config.Logger?.LogTrace("NexNetSession.StartAsServer()");
        return StartReadAsync();
    }

    private async ValueTask<bool> TryReconnectAsClient()
    {
        _config.Logger?.LogTrace("NexNetSession.TryReconnectAsClient()");
        if (_isServer)
            return false;


        var clientConfig = Unsafe.As<ClientConfig>(_config);
        var clientHub = Unsafe.As<ClientHubBase<TProxy>>(_hub);

        await clientHub.Reconnecting();
        int count = 0;

        while (true)
        {
            ITransportBase? transport = null;
            try
            {
                var delay = clientConfig.ReconnectionPolicy.ReconnectDelay(count++);

                if (delay == null)
                    return false;

                await Task.Delay(delay.Value);

                _config.Logger?.LogTrace($"Reconnection attempt {count}");

                transport = await clientConfig.ConnectTransport();

                _pipeInput = transport.Input;
                _pipeOutput = transport.Output;
                _transportConnection = transport;

                _isReconnected = true;
                await StartAsClient();
                return true;
            }
            catch (Exception e)
            {
                transport?.Dispose();
            }
        }

    }

    private async Task DisconnectCore(DisconnectReason reason, bool sendDisconnect)
    {
        if (_pipeInput == null)
            return;

        _config.Logger?.LogTrace($"NexNetSession.DisconnectCore({reason}, {sendDisconnect})");

        // ReSharper disable once MethodHasAsyncOverload
        _pipeInput!.Complete();
        _pipeInput = null;

        if (sendDisconnect && !_config.InternalForceDisableSendingDisconnectSignal)
        {
            await SendHeader((MessageType)reason);

            if (_config.DisconnectDelay > 0)
            {
                // Add a delay in here to ensure that the data has a chance to send on the wire before a full disconnection.
                await Task.Delay(_config.DisconnectDelay);
            }
        }


        if (_config.InternalNoLingerOnShutdown)
        {
            _transportConnection.Socket.LingerState = new LingerOption(true, 0);
            _transportConnection.Socket.Close(0);
            _pipeInput = null;
            return;
        }
        else
        {
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
            _transportConnection.Dispose();
        }

        // If we match a limited type of disconnects, attempt to reconnect if we are the client
        if (_isServer == false
            && reason == DisconnectReason.DisconnectSocketError
            || reason == DisconnectReason.Timeout
            || reason == DisconnectReason.DisconnectServerRestarting)
        {
            // If we reconnect, stop the disconnection process.
            if (await TryReconnectAsClient())
                return;
        }
        
        _hub.Disconnected(new DisconnectReasonException(reason, null));
        OnDisconnected?.Invoke();

        _sessionManager?.UnregisterSession(this);
        ((IDisposable)SessionStore).Dispose();
        _invocationTaskArgumentsPool.Clear();
    }

    internal class InvocationTaskArguments
    {
        public InvocationRequestMessage Message { get; set; }
        public NexNetSession<THub, TProxy> Session { get; set; }
    }
}
