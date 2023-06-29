using System.Buffers;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Invocation;
using System.Threading.Tasks;
using System;
using System.ComponentModel.Design;
using System.Runtime.CompilerServices;

namespace NexNet.Internals;

internal partial class NexNetSession<THub, TProxy> : INexNetSession<TProxy>
    where THub : HubBase<TProxy>, IMethodInvoker<TProxy>, IInvocationMethodHash
    where TProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
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

                var processResult = await Process(result.Buffer).ConfigureAwait(false);

                _config.Logger?.LogTrace($"Reading completed.");

                if (processResult.DisconnectReason != DisconnectReason.None)
                {
                    await DisconnectCore(processResult.DisconnectReason, processResult.IssueDisconnectMessage).ConfigureAwait(false);
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

                _pipeInput?.AdvanceTo(processResult.Position);
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

    private async ValueTask<ProcessResult> Process(ReadOnlySequence<byte> sequence)
    {
        var position = 0;
        var maxLength = sequence.Length;
        var disconnect = DisconnectReason.None;
        var issueDisconnectMessage = true;
        var breakLoop = false;
        while (true)
        {
            if (_recMessageHeader.Type == MessageType.Unset || _recMessageHeader.BodyLength == -1)
            {
                if (_recMessageHeader.PostHeaderLength == 0)
                {
                    if (position >= maxLength)
                    {
                        _config.Logger?.LogTrace($"Could not read next header type. No more data.");
                        break;
                    }

                    var type = _recMessageHeader.Type = (MessageType)sequence.Slice(position, 1).FirstSpan[0];
                    position++;
                    _config.Logger?.LogTrace($"Received {type} header.");

                    switch (type)
                    {
                        // SINGLE BYTE HEADER ONLY
                        case MessageType.Ping:
                            _recMessageHeader.Reset();

                            // If we are the server, send back a ping message to help the client know if it is still connected.
                            if (_isServer)
                                await SendHeader(MessageType.Ping).ConfigureAwait(false);

                            continue;

                        case MessageType.DisconnectSocketError:
                        case MessageType.DisconnectGraceful:
                        case MessageType.DisconnectProtocolError:
                        case MessageType.DisconnectTimeout:
                        case MessageType.DisconnectClientHubMismatch:
                        case MessageType.DisconnectServerHubMismatch:
                        case MessageType.DisconnectServerShutdown:
                        case MessageType.DisconnectAuthentication:
                        case MessageType.DisconnectServerRestarting:
                            _config.Logger?.LogTrace($"Received disconnect message.");
                            // Translate the type over to the reason.
                            disconnect = (DisconnectReason)_recMessageHeader.Type;
                            issueDisconnectMessage = false;
                            breakLoop = true;
                            break;

                        // HEADER + BODY
                        case MessageType.GreetingClient:
                        case MessageType.GreetingServer:
                        case MessageType.InvocationWithResponseRequest:
                        case MessageType.InvocationCancellationRequest:
                        case MessageType.InvocationProxyResult:
                            _config.Logger?.LogTrace($"Message has a standard body.");
                            _recMessageHeader.PostHeaderLength = 2;
                            _recMessageHeader.BodyLength = 0;
                            break;

                        //case MessageType.PipeChannelOpen:
                        //    _config.Logger?.LogTrace($"PipeChannel opened.");
                        //    break;
                        case MessageType.PipeChannelWrite:
                            _recMessageHeader.PostHeaderLength = 4;
                            _recMessageHeader.BodyLength = 0;
                            _config.Logger?.LogTrace($"PipeChannel received data.");

                            break;
                        case MessageType.PipeChannelClose:
                            _config.Logger?.LogTrace($"PipeChannel closed.");
                            break;

                        default:
                            _config.Logger?.LogTrace($"received invalid MessageHeader '{type}'.");
                            // If we are outside of the acceptable messages, disconnect the connection.
                            disconnect = DisconnectReason.ProtocolError;
                            break;
                    }

                    if (breakLoop)
                        break;
                }

                if (_recMessageHeader.PostHeaderLength > 0 || _recMessageHeader.BodyLength == 0)
                {
                    // Check to see if we have the minimum amount of data to read for the header.
                    ar totalHeaderToRead = (_recMessageHeader.PostHeaderLength > 0
                        ? _recMessageHeader.PostHeaderLength
                        : 0) + _recMessageHeader.BodyLength;

                    if (position + _recMessageHeader.PostHeaderLength > maxLength)
                    {
                        _config.Logger?.LogTrace($"Could not read the next {_recMessageHeader.PostHeaderLength} bytes for the {_recMessageHeader.Type} header. Not enough data.");
                        break;
                    }

                    // If we have a body length of 0 here, it is needing to be read.
                    // -1 indicates that there is no body length to read.
                    if (_recMessageHeader.BodyLength == 0)
                    {
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
        return new ProcessResult(seqPosition, disconnect, issueDisconnectMessage);
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
        }
        else if (message is ServerGreetingMessage)
        {
            // Verify that this is the client
            if (_isServer)
                return DisconnectReason.ProtocolError;

            _ = Task.Factory.StartNew(InvokeOnConnected, this);

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
}
