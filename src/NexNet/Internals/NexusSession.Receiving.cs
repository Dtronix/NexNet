using System.Buffers;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Invocation;
using System.Threading.Tasks;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NexNet.Logging;

namespace NexNet.Internals;

internal partial class NexusSession<TNexus, TProxy>
{
    public async Task StartReadAsync(CancellationToken cancellationToken = default)
    {
        _ = Task.Delay(Config.HandshakeTimeout, cancellationToken).ContinueWith(CheckHandshakeComplete, cancellationToken);
        
        Logger?.LogTrace("Reading");
        try
        {
            while (true)
            {
                if (_pipeInput == null
                    || State != ConnectionState.Connected)
                    return;

                var result = await _pipeInput.ReadAsync(cancellationToken).ConfigureAwait(false);
                LastReceived = Environment.TickCount64;
                
                var buffer = result.Buffer;
                // Terribly inefficient and only used for testing
                if(Config.InternalOnReceive != null)
                    await Config.InternalOnReceive.Invoke(this, buffer).ConfigureAwait(false);
                
                // Confirm that this is a NexNet transport.
                if ((_internalState & InternalState.ProtocolConfirmed) == 0)
                {
                    Logger?.LogTrace("Reading protocol header...");
                    if (ConfirmProtocol(buffer, out var disconnect))
                    {
                        // Push the buffer forward.
                        buffer = buffer.Slice(8, buffer.End);
                        
                        // Don't process an empty buffer.
                        if (buffer.Length == 0)
                        {
                            _pipeInput?.AdvanceTo(buffer.Start, buffer.Start);
                            continue;
                        }
                    }
                    else
                    {
                        // If the disconnect is none, that means there was not enough data to read for the header.
                        // We will try again.  If there is a disconnection reason, disconnect.
                        if (disconnect != DisconnectReason.None)
                        {
                            await DisconnectCore(disconnect, true).ConfigureAwait(false);
                            return;
                        }
                        
                        _pipeInput?.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                        continue;
                    }
                }

                var processResult = await Process(buffer).ConfigureAwait(false);

                //_config.Logger?.LogTrace($"Reading completed.");

                if (processResult.DisconnectReason != DisconnectReason.None)
                {
                    await DisconnectCore(processResult.DisconnectReason, processResult.IssueDisconnectMessage).ConfigureAwait(false);
                    return;
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    Logger?.LogTrace($"Reading completed with IsCompleted: {result.IsCompleted} and IsCanceled: {result.IsCanceled}.");

                    if (_registeredDisconnectReason == DisconnectReason.None)
                    {
                        Logger?.LogTrace("Disconnected without a reason.");

                        // If there is not a disconnect reason, then we disconnected for an unknown reason and should 
                        // be allowed to reconnect.
                        await DisconnectCore(DisconnectReason.SocketError, false).ConfigureAwait(false);
                    }
                    return;
                }

                _pipeInput?.AdvanceTo(processResult.Position, result.Buffer.End);
            }
        }
        catch (NullReferenceException ex)
        {
            // Log NullReferenceException as it may indicate a bug
            if (State != ConnectionState.Disconnecting)
            {
                Logger?.LogError(ex, "Unexpected NullReferenceException during receive processing");
                await DisconnectCore(DisconnectReason.ProtocolError, false).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException ex)
        {
            // Log ObjectDisposedException for debugging
            Logger?.LogDebug(ex, "Session disposed during receive processing");
        }
        catch (Exception ex)
        {
            if (State != ConnectionState.Disconnecting)
            {
                Logger?.LogError(ex, "Reading exited with exception.");
                await DisconnectCore(DisconnectReason.SocketError, false).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<ProcessResult> Process(ReadOnlySequence<byte> sequence)
    {
        var position = 0;
        var maxLength = sequence.Length;
        var disconnect = DisconnectReason.None;
        var issueDisconnectMessage = true;
        var breakLoop = false;
        while (State == ConnectionState.Connected)
        {
            if (_recMessageHeader.IsHeaderComplete == false)
            {
                if (_recMessageHeader.Type == MessageType.Unset)
                {
                    if (position >= maxLength)
                    {
                        //_config.Logger?.LogTrace($"Could not read next header type. No more data.");
                        break;
                    }

                    var headerMessageTypeByte = sequence.Slice(position, 1).FirstSpan[0];
                    
                    // This is a disconnect message
                    if (headerMessageTypeByte is >= 20 and < 39)
                        return new ProcessResult(new SequencePosition(), (DisconnectReason)headerMessageTypeByte, false);
                    
                    _recMessageHeader.Type = (MessageType)headerMessageTypeByte;
                    
                    // Ensure that the connection is completed.
                    // If not, that the server/client/reconnection greeting  is the first message received.
                    if ((_internalState & InternalState.NexusCompletedConnection) == 0 
                        && (_recMessageHeader.Type != MessageType.ClientGreeting
                            && _recMessageHeader.Type != MessageType.ServerGreeting
                            && _recMessageHeader.Type != MessageType.ClientGreetingReconnection))
                    {
                        _config.Logger?.LogError($"Received {_recMessageHeader.Type} request prior to connection completion");
                        return new ProcessResult(new SequencePosition(), DisconnectReason.ProtocolError, true);
                    }
                    
                    position++;
                    //Logger?.LogTrace($"Received {_recMessageHeader.Type} header.");

                    switch (_recMessageHeader.Type)
                    {
                        // SINGLE BYTE HEADER ONLY
                        case MessageType.Ping:
                            _recMessageHeader.Reset();

                            // If we are the server, send back a ping message to help the client know if it is still connected.
                            if (IsServer)
                                await SendHeader(MessageType.Ping).ConfigureAwait(false);

                            continue;
                        
                        // HEADER + BODY
                        case MessageType.ServerGreeting:
                        case MessageType.ClientGreeting:
                        case MessageType.ClientGreetingReconnection:
                        case MessageType.Invocation:
                        case MessageType.InvocationCancellation:
                        case MessageType.InvocationResult:
                        case MessageType.DuplexPipeUpdateState:
                            //Logger?.LogTrace($"Message has a standard body.");
                            _recMessageHeader.SetTotalHeaderSize(0, true);
                            break;
   
                        case MessageType.DuplexPipeWrite:
                            _recMessageHeader.SetTotalHeaderSize(sizeof(ushort), true);
                            break;

                        default:
                            Logger?.LogInfo($"Received invalid MessageHeader '{_recMessageHeader.Type}'.");
                            // If we are outside the acceptable messages, disconnect the connection.
                            disconnect = DisconnectReason.ProtocolError;
                            break;
                    }

                    if (breakLoop)
                        break;
                }
                // Checks for where we can receive what type of message
                if (IsServer)
                {
                    // Ensure we are not receiving messages intended for the client on the server.
                    if (_recMessageHeader.Type is MessageType.ServerGreeting)
                    {
                        disconnect = DisconnectReason.ProtocolError;
                        break;
                    }
                }
                else
                {
                    // Ensure we are not receiving messages intended for the server on the client.
                    if (_recMessageHeader.Type is MessageType.ClientGreeting or MessageType.ClientGreetingReconnection)
                    {
                        disconnect = DisconnectReason.ProtocolError;
                        break;
                    }
                }

                // If the whole header can't be read, loop back around.
                if (position + _recMessageHeader.TotalHeaderLength > maxLength)
                {
                    Logger?.LogTrace(
                        $"Could not read the next {_recMessageHeader.PostHeaderLength} bytes for the {_recMessageHeader.Type} header. Not enough data.");
                    break;
                }

                // If we have a body length of 0 here, it is needing to be read.
                // -1 indicates that there is no body length to read.
                if (_recMessageHeader.BodyLength == 0)
                {
                    if (!ReadingHelpers.TryReadUShort(sequence, _readBuffer, ref position, out var bodyLength))
                    {
                        Logger?.LogTrace($"Could not read body length.");
                        disconnect = DisconnectReason.ProtocolError;
                        break;
                    }

                    //Logger?.LogTrace($"Parsed body length of {_recMessageHeader.BodyLength}.");
                    _recMessageHeader.BodyLength = bodyLength;
                }

                // Read the post header.
                if (_recMessageHeader.PostHeaderLength > 0)
                {
                    switch (_recMessageHeader.Type)
                    {
                        case MessageType.DuplexPipeWrite:
                            if (!ReadingHelpers.TryReadUShort(sequence, _readBuffer, ref position,
                                    out _recMessageHeader.DuplexPipeId))
                            {
                                Logger?.LogTrace($"Could not read invocation id for {_recMessageHeader.Type}.");
                                disconnect = DisconnectReason.ProtocolError;
                                break;
                            }

                            //Logger?.LogTrace(
                            //    $"Parsed DuplexStreamId of {_recMessageHeader.DuplexPipeId} for {_recMessageHeader.Type}.");
                            break;
                        default:
                            Logger?.LogTrace(
                                $"Received invalid combination of PostHeaderLength ({_recMessageHeader.PostHeaderLength}) and MessageType ({_recMessageHeader.Type}).");
                            // If we are outside the acceptable messages, disconnect the connection.
                            disconnect = DisconnectReason.ProtocolError;
                            break;

                    }
                }

                _recMessageHeader.IsHeaderComplete = true;
            }

            // Read the body.
            if (position + _recMessageHeader.BodyLength > maxLength)
            {
                Logger?.LogTrace($"Could not read all the {_recMessageHeader.BodyLength} body bytes.");
                break;
            }

            ReadOnlySequence<byte> bodySlice;
            try
            {
                bodySlice = sequence.Slice(position, _recMessageHeader.BodyLength);
            }
            catch (Exception e)
            {
                Logger?.LogCritical(e,
                    $"Attempted to read beyond the end of the available sequence. Length: {sequence.Length}; Position: {position}; BodyLength: {_recMessageHeader.BodyLength}");
                disconnect = DisconnectReason.ProtocolError;
                break;
            }

            position += _recMessageHeader.BodyLength;
            IMessageBase? messageBody = null;
            bool disposeMessage = true;
            try
            {
                switch (_recMessageHeader.Type)
                {
                    case MessageType.ServerGreeting:
                    case MessageType.ClientGreeting:
                    case MessageType.ClientGreetingReconnection:
                    case MessageType.InvocationCancellation:
                    case MessageType.DuplexPipeUpdateState:
                        // TODO: Review transitioning this to a simple message instead of a full message.
                        messageBody = _cacheManager.Deserialize(_recMessageHeader.Type, bodySlice);
                        break;

                    case MessageType.Invocation:
                        // Special case for invocation result, as it is passed to the method and handled/disposed there.
                        disposeMessage = false;
                        messageBody = _cacheManager.Deserialize(_recMessageHeader.Type, bodySlice);
                        break;

                    case MessageType.InvocationResult:
                        disposeMessage = false;
                        messageBody = _cacheManager.Deserialize(_recMessageHeader.Type, bodySlice);
                        break;

                    case MessageType.DuplexPipeWrite:
                    {
                        await PipeManager.BufferIncomingData(_recMessageHeader.DuplexPipeId, bodySlice)
                            .ConfigureAwait(false);
                        break;
                    }

                    default:
                        Logger?.LogError($"Deserialized type not recognized. {_recMessageHeader.Type}.");
                        disconnect = DisconnectReason.ProtocolError;
                        break;
                }

                // Used only when the header type is not recognized.
                if (disconnect != DisconnectReason.None)
                    break;

                // If we have a message body in the form of a MemoryPack, pass it to the message handler.
                if (messageBody != null)
                {
                    Logger?.LogTrace($"Handling {_recMessageHeader.Type} message.");
                    disconnect = await MessageHandler(messageBody!, _recMessageHeader.Type).ConfigureAwait(false);
                }

                if (disconnect != DisconnectReason.None)
                {
                    Logger?.LogTrace($"Message could not be handled and disconnected with {disconnect}");
                    break;
                }

                //_config.Logger?.LogTrace($"Resetting header.");
                // Reset the header.
                _recMessageHeader.Reset();
            }
            catch (Exception e)
            {
                Logger?.LogTrace(e, "Could not deserialize body.");
                //_logger?.LogError(e, $"Could not deserialize body..");
                disconnect = DisconnectReason.ProtocolError;
                break;
            }
            finally
            {
                if (disposeMessage)
                    messageBody?.Dispose();
            }

            _recMessageHeader.Reset();
        }

        var seqPosition = sequence.GetPosition(position);
        return new ProcessResult(seqPosition, disconnect, issueDisconnectMessage);
    }

    private bool ConfirmProtocol(ReadOnlySequence<byte> sequence, out DisconnectReason disconnect)
    {
        // Try again until we have enough data.
        if (sequence.Length < 8)
        {
            disconnect = DisconnectReason.None;
            return false;
        }

        var headerSlice = sequence.Slice(0, 8);
        Span<byte> header = stackalloc byte[8]; 
        headerSlice.CopyTo(header);
        var receivedProtocolTag = BitConverter.ToUInt32(header);
        var reserved1 = header[4]; // Reserved for future
        var reserved2 = header[5]; // Reserved for future
        var reserved3 = header[6]; // Reserved for future
        var receivedProtocolVersion = header[7];
        
        if (receivedProtocolTag != ProtocolTag)
        {
            Logger?.LogTrace("Transport data is not a NexNet stream.");
            disconnect = DisconnectReason.ProtocolError;
            return false;
        }
        
        // Ensure the reserved values are 0.
        if (reserved1 != 0 || reserved2 != 0 || reserved3 != 0)
        {
            Logger?.LogTrace("Reserved data is not empty as required for NexNet stream.");
            disconnect = DisconnectReason.ProtocolError;
            return false;
        }

        if (receivedProtocolVersion != ProtocolVersion)
        {
            Logger?.LogTrace("Transport version is out of the range of valid versions.");
            disconnect = DisconnectReason.ProtocolError;
            return false;
        }

        EnumUtilities<InternalState>.SetFlag(ref _internalState, InternalState.ProtocolConfirmed);
        disconnect = DisconnectReason.None;
        return true;
    }


    private async ValueTask<DisconnectReason> MessageHandler(IMessageBase message, MessageType messageType)
    {
        static async void InvokeOnConnected(object? sessionObj)
        {
            var session = Unsafe.As<NexusSession<TNexus, TProxy>>(sessionObj)!;

            try
            {
                await session._nexus.Connected((session._internalState & InternalState.ReconnectingInProgress) != 0).ConfigureAwait(false);
            }
            catch (TaskCanceledException e)
            {
                session.Logger?.LogTrace(e, "OnConnected was canceled.");
            }
            catch (Exception e)
            {
                session.Logger?.LogError(e, "OnConnected threw an exception.");
            }
            // Reset the value;
            EnumUtilities<InternalState>.RemoveFlag(ref session._internalState, InternalState.ReconnectingInProgress);
        }

        switch (messageType)
        {
            case MessageType.ClientGreetingReconnection:
                /*
                 TODO: Complete updated reconnection logic that will preserve the Nexus.
                var initialValue = EnumUtilities<InternalState>.SetFlag(ref _internalState,
                    InternalState.InitialClientGreetingReceived);

                if ((initialValue & InternalState.InitialClientGreetingReceived) == 0)
                {
                    _config.Logger?.LogError("Client attempted to reconnect before the client has ever completed the connection process.");
                    return DisconnectReason.ProtocolError;
                }

                goto ClientGreetingHandler;
                */
            case MessageType.ClientGreeting:
            {
                // Fix 2.7: Rate limiting on greeting messages
                if (Interlocked.Increment(ref _greetingAttempts) > MaxGreetingAttempts)
                {
                    _config.Logger?.LogWarning("Too many greeting attempts, disconnecting");
                    return DisconnectReason.ProtocolError;
                }

                // Set the initial flag for the greeting and
                // ensure we are not re-connecting with a simple ClientGreeting.
                if ((EnumUtilities<InternalState>.SetFlag(
                        ref _internalState, InternalState.InitialClientGreetingReceived) & InternalState.InitialClientGreetingReceived) != 0)
                {
                    _config.Logger?.LogError("Client attempted to connect with another ClientGreeting rather than the required ClientGreetingReconnection message.");
                    return DisconnectReason.ProtocolError;
                }

                IClientGreetingMessageBase cGreeting = messageType == MessageType.ClientGreeting
                    ? message.As<ClientGreetingMessage>()
                    : message.As<ClientGreetingReconnectionMessage>();

                // Verify that this is the server
                if (!IsServer)
                    return DisconnectReason.ProtocolError;

                // Fix 2.3: Validate authentication token size
                if (cGreeting.AuthenticationToken.Length > MaxAuthenticationTokenSize)
                {
                    _config.Logger?.LogWarning($"Authentication token exceeds maximum size: {cGreeting.AuthenticationToken.Length} > {MaxAuthenticationTokenSize}");
                    return DisconnectReason.ProtocolError;
                }

                // Fix 2.8: Validate version string length
                if (cGreeting.Version != null && cGreeting.Version.Length > MaxVersionStringLength)
                {
                    _config.Logger?.LogWarning($"Version string exceeds maximum length: {cGreeting.Version.Length} > {MaxVersionStringLength}");
                    return DisconnectReason.ProtocolError;
                }

                if (cGreeting.ClientNexusHash != TProxy.MethodHash)
                {
                    return DisconnectReason.ClientMismatch;
                }

                var requestedVersion = cGreeting.Version;
                
                // If we have no versions, ensure that the requested version is null.
                // Start with teh default Nexus hash.
                int verificationHash = TNexus.MethodHash;
                if (TNexus.VersionHashTable.Count == 0)
                {
                    // Verify that the requested version is null.  If not, the client is expecting 
                    // a specific version that may or may not ve available.
                    if(requestedVersion != null)
                        return DisconnectReason.ServerMismatch;

                    // Reaching here means the server is not versioning, and the client is not
                    // expecting a specific version. We are good to continue.
                }
                else
                {
                    if(requestedVersion == null)
                        return DisconnectReason.ServerMismatch;
                        
                    // Ensure the server has this version available.
                    if(!TNexus.VersionHashTable.TryGetValue(requestedVersion, out verificationHash))
                        return DisconnectReason.ServerMismatch;
                }
                
                // Validate the hash matches
                if(verificationHash != cGreeting.ServerNexusHash)
                    return DisconnectReason.ServerMismatch;
                
                // Validation has succeeded.
                _versionHash = verificationHash;

                var serverConfig = Unsafe.As<ServerConfig>(_config);

                // See if there is an authentication handler.
                if (serverConfig.Authenticate)
                {
                    // Run the handler and verify that it is good.
                    var serverNexus = Unsafe.As<ServerNexusBase<TProxy>>(_nexus);

                    try
                    {
                        Identity = await serverNexus.Authenticate(cGreeting.AuthenticationToken).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        // Exception was thrown. Cancel the entire connection.
                        _config.Logger?.LogError(e, "Authenticate threw an exception.");
                        return DisconnectReason.Authentication;
                    }
                    

                    // Set the identity on the context.
                    var serverContext = Unsafe.As<ServerSessionContext<TProxy>>(_nexus.SessionContext);
                    serverContext.Identity = Identity;

                    // If the token is not good, disconnect.
                    if (Identity == null)
                    {
                        return DisconnectReason.Authentication;
                    }
                }
                
                EnumUtilities<InternalState>.SetFlag(
                    ref _internalState, InternalState.NexusCompletedConnection);

                await _sessionManager!.Sessions.RegisterSessionAsync(this).ConfigureAwait(false);
                
                await Unsafe.As<ServerNexusBase<TProxy>>(_nexus).NexusInitialize().ConfigureAwait(false);

                using var serverGreeting = _cacheManager.Rent<ServerGreetingMessage>();
                serverGreeting.Version = TProxy.MethodHash;
                serverGreeting.ClientId = Id;

                await SendMessage(serverGreeting).ConfigureAwait(false);
                
                _ = Task.Factory.StartNew(InvokeOnConnected, this);

                _readyTaskCompletionSource?.TrySetResult();
                break;
            }

            case MessageType.ServerGreeting:
            {
                if ((EnumUtilities<InternalState>.SetFlag(
                        ref _internalState, InternalState.InitialServerGreetingReceived) & InternalState.InitialServerGreetingReceived) != 0)
                {
                    _config.Logger?.LogError("Server sent multiple server greetings.");
                    return DisconnectReason.ProtocolError;
                }
                // Verify that this is the client
                if (IsServer)
                {
                    _config.Logger?.LogError($"Received {messageType} message on the server.");
                    return DisconnectReason.ProtocolError;
                }

                var greetingMessage = message.As<ServerGreetingMessage>();

                if (greetingMessage.Version != TNexus.MethodHash)
                    return DisconnectReason.ClientMismatch;
                
                // There is no versioning on the client so we can just use the hash directly.
                _versionHash = TNexus.MethodHash;
                
                // Set the server assigned client id.
                Id = greetingMessage.ClientId;

                //Logger = Logger.CreateLogger(Id.ToString());
   
                


                EnumUtilities<InternalState>.SetFlag(
                    ref _internalState, InternalState.NexusCompletedConnection);
                
                _ = Task.Factory.StartNew(InvokeOnConnected, this);
                
                _readyTaskCompletionSource?.TrySetResult();
                break;

            }

            case MessageType.Invocation:
            {
                var invocationRequestMessage = message.As<InvocationMessage>();
                // Throttle invocations.
                
                var hash = ((long)_versionHash << 16) | invocationRequestMessage.MethodId;
                if (!TNexus.VersionMethodHashSet.Contains(hash))
                {
                    Logger?.LogWarning($"Session attempted to invoke method {invocationRequestMessage.MethodId} but that method can't be invoked on API version {_versionHash}.");
                    return DisconnectReason.ProtocolError;
                }
                Logger?.LogTrace($"Started Invoking method {invocationRequestMessage.MethodId}.");
                
                await _invocationSemaphore.WaitAsync(_disconnectionCts.Token).ConfigureAwait(false);

                if (State != ConnectionState.Connected)
                    return DisconnectReason.ProtocolError;

                if (!_invocationTaskArgumentsPool.TryTake(out var args))
                    args = new InvocationTaskArguments();

                args.Message = invocationRequestMessage;
                args.Session = this;

                _ = Task.Factory.StartNew(InvocationTask, args);
                break;
            }

            case MessageType.InvocationResult:
            {
                var invocationProxyResultMessage = message.As<InvocationResultMessage>();
                SessionInvocationStateManager.UpdateInvocationResult(invocationProxyResultMessage);
                break;
            }

            case MessageType.InvocationCancellation:
            {
                var invocationCancellationRequestMessage = message.As<InvocationCancellationMessage>();
                _nexus.CancelInvocation(invocationCancellationRequestMessage);
                break;
            }

            case MessageType.DuplexPipeUpdateState:
            {
                var updateStateMessage = message.As<DuplexPipeUpdateStateMessage>();
                var updateStateResult = PipeManager.UpdateState(updateStateMessage.PipeId, updateStateMessage.State);
                if (updateStateResult != DisconnectReason.None)
                    return updateStateResult;
                break;
            }

            default:
                // If we don't know what the message is, then disconnect the connection
                // as we have received invalid/unexpected data.
                return DisconnectReason.ProtocolError;
        }
        return DisconnectReason.None;
        
        static async void InvocationTask(object? argumentsObj)
        {
            var arguments = Unsafe.As<InvocationTaskArguments>(argumentsObj)!;

            var session = arguments.Session;
            var message = arguments.Message;
            try
            {
                arguments.Session.Logger?.LogTrace($"Invoking method {message.MethodId}.");
                await session._nexus.InvokeMethod(message).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                session._config.Logger?.LogError(e, $"Invoked method {message.MethodId} threw exception");
            }
            finally
            {
                try
                {
                    session._invocationSemaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // The semaphore was disposed and the client is disconnected.
                }


                // Clear out the references before returning to the pool.
                arguments.Session = null!;
                arguments.Message = null!;
                session._invocationTaskArgumentsPool.Add(arguments);
            }
        }
    }
}
