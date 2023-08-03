using System.Buffers;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Invocation;
using System.Threading.Tasks;
using System;
using System.ComponentModel.Design;
using System.Drawing;
using System.Runtime.CompilerServices;
using NexNet.Internals.Pipes;
using Pipelines.Sockets.Unofficial.Arenas;

namespace NexNet.Internals;

internal partial class NexusSession<TNexus, TProxy> : INexusSession<TProxy>
    where TNexus : NexusBase<TProxy>, IMethodInvoker, IInvocationMethodHash
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

                // Terribly inefficient and only used for testing
                Config.InternalOnReceive?.Invoke(this, result.Buffer);

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

                _pipeInput?.AdvanceTo(processResult.Position, result.Buffer.End);
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
        while (State == ConnectionState.Connected)
        {
            if (_recMessageHeader.IsHeaderComplete == false)
            {
                if (_recMessageHeader.Type == MessageType.Unset)
                {
                    if (position >= maxLength)
                    {
                        _config.Logger?.LogTrace($"Could not read next header type. No more data.");
                        break;
                    }

                    _recMessageHeader.Type = (MessageType)sequence.Slice(position, 1).FirstSpan[0];
                    position++;
                    _config.Logger?.LogTrace($"Received {_recMessageHeader.Type} header.");

                    switch (_recMessageHeader.Type)
                    {
                        // SINGLE BYTE HEADER ONLY
                        case MessageType.Ping:
                            _recMessageHeader.Reset();

                            // If we are the server, send back a ping message to help the client know if it is still connected.
                            if (IsServer)
                                await SendHeader(MessageType.Ping).ConfigureAwait(false);

                            continue;

                        case MessageType.DisconnectSocketError:
                        case MessageType.DisconnectGraceful:
                        case MessageType.DisconnectProtocolError:
                        case MessageType.DisconnectTimeout:
                        case MessageType.DisconnectClientMismatch:
                        case MessageType.DisconnectServerMismatch:
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
                        case MessageType.ClientGreeting:
                        case MessageType.ServerGreeting:
                        case MessageType.Invocation:
                        case MessageType.InvocationCancellation:
                        case MessageType.InvocationResult:
                        case MessageType.DuplexPipeUpdateState:
                            _config.Logger?.LogTrace($"Message has a standard body.");
                            _recMessageHeader.SetTotalHeaderSize(0, true);
                            break;

                        case MessageType.DuplexPipeWrite:
                            _recMessageHeader.SetTotalHeaderSize(sizeof(ushort), true);
                            _config.Logger?.LogTrace($"DuplexNexusPipe received data.");
                            break;

                        default:
                            _config.Logger?.LogTrace($"Received invalid MessageHeader '{_recMessageHeader.Type}'.");
                            // If we are outside of the acceptable messages, disconnect the connection.
                            disconnect = DisconnectReason.ProtocolError;
                            break;
                    }

                    if (breakLoop)
                        break;
                }

                // If the whole header can't be read, loop back around.
                if (position + _recMessageHeader.TotalHeaderLength > maxLength)
                {
                    _config.Logger?.LogTrace(
                        $"Could not read the next {_recMessageHeader.PostHeaderLength} bytes for the {_recMessageHeader.Type} header. Not enough data.");
                    break;
                }

                // If we have a body length of 0 here, it is needing to be read.
                // -1 indicates that there is no body length to read.
                if (_recMessageHeader.BodyLength == 0)
                {
                    if (!ReadingHelpers.TryReadUShort(sequence, _readBuffer, ref position, out var bodyLength))
                    {
                        _config.Logger?.LogTrace($"Could not read body length.");
                        disconnect = DisconnectReason.ProtocolError;
                        break;
                    }

                    _config.Logger?.LogTrace($"Parsed body length of {_recMessageHeader.BodyLength}.");
                    _recMessageHeader.BodyLength = bodyLength;
                }

                // Read the post header.
                if (_recMessageHeader.PostHeaderLength > 0)
                {
                    switch (_recMessageHeader.Type)
                    { 
                        case MessageType.DuplexPipeWrite:
                            if (!ReadingHelpers.TryReadUShort(sequence, _readBuffer, ref position, out _recMessageHeader.DuplexPipeId))
                            {
                                _config.Logger?.LogTrace($"Could not read invocation id for {_recMessageHeader.Type}.");
                                disconnect = DisconnectReason.ProtocolError;
                                break;
                            }

                            _config.Logger?.LogTrace($"Parsed DuplexStreamId of {_recMessageHeader.DuplexPipeId} for {_recMessageHeader.Type}.");
                            break;
                        default:
                            _config.Logger?.LogTrace($"Received invalid combination of PostHeaderLength ({_recMessageHeader.PostHeaderLength}) and MessageType ({_recMessageHeader.Type}).");
                            // If we are outside of the acceptable messages, disconnect the connection.
                            disconnect = DisconnectReason.ProtocolError;
                            break;

                    }
                }

                _recMessageHeader.IsHeaderComplete = true;
            }

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
                IMessageBase? messageBody = null;
                switch (_recMessageHeader.Type)
                {
                    case MessageType.ServerGreeting:
                    case MessageType.ClientGreeting:
                    case MessageType.Invocation:
                    case MessageType.InvocationResult:
                    case MessageType.InvocationCancellation:
                    case MessageType.DuplexPipeUpdateState:
                        messageBody = _cacheManager.Deserialize(_recMessageHeader.Type, bodySlice);
                        break;

                    case MessageType.DuplexPipeWrite:
                    {
                        var bufferDuplexPipeResult = await PipeManager.BufferIncomingData(_recMessageHeader.DuplexPipeId, bodySlice);
                        if (bufferDuplexPipeResult == NexusPipeBufferResult.HighCutoffReached)
                        {
                            disconnect = DisconnectReason.NexusPipeHighWaterCutoffReached;
                        }

                        break;
                    }

                    default:
                        _config.Logger?.LogError(
                            $"Deserialized type not recognized. {_recMessageHeader.Type}.");
                        disconnect = DisconnectReason.ProtocolError;
                        break;
                }

                // Used only when the header type is not recognized.
                if (disconnect != DisconnectReason.None)
                    break;

                // If we have a message body in the form of a MemoryPack, pass it to the message handler.
                if (messageBody != null)
                {
                    _config.Logger?.LogTrace($"Handling {_recMessageHeader.Type} message.");
                    disconnect = await MessageHandler(messageBody!, _recMessageHeader.Type).ConfigureAwait(false);
                }

                if (disconnect != DisconnectReason.None)
                {
                    _config.Logger?.LogTrace(
                        $"Message could not be handled and disconnected with {disconnect}");
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

        var seqPosition = sequence.GetPosition(position);
        return new ProcessResult(seqPosition, disconnect, issueDisconnectMessage);
    }



    private async ValueTask<DisconnectReason> MessageHandler(IMessageBase message, MessageType messageType)
    {
        static async void InvokeOnConnected(object? sessionObj)
        {
            var session = Unsafe.As<NexusSession<TNexus, TProxy>>(sessionObj)!;
            await session._nexus.Connected(session._isReconnected).ConfigureAwait(false);

            // Reset the value;
            session._isReconnected = false;
        }

        switch (messageType)
        {
            case MessageType.ClientGreeting:
            {
                var cGreeting = message.As<ClientGreetingMessage>();
                // Verify that this is the server
                if (!IsServer)
                    return DisconnectReason.ProtocolError;

                // Verify what the greeting method hashes matches this nexus's and proxy's
                if (cGreeting.ServerNexusMethodHash != TNexus.MethodHash)
                {
                    CacheManager.Return(cGreeting);
                    return DisconnectReason.ServerMismatch;
                }

                if (cGreeting.ClientNexusMethodHash != TProxy.MethodHash)
                {
                    CacheManager.Return(cGreeting);
                    return DisconnectReason.ClientMismatch;
                }

                var serverConfig = Unsafe.As<ServerConfig>(_config);

                // See if there is an authentication handler.
                if (serverConfig.Authenticate)
                {
                    // Run the handler and verify that it is good.
                    var serverNexus = Unsafe.As<ServerNexusBase<TProxy>>(_nexus);
                    Identity = await serverNexus.Authenticate(cGreeting.AuthenticationToken);

                    // Set the identity on the context.
                    var serverContext = Unsafe.As<ServerSessionContext<TProxy>>(_nexus.SessionContext);
                    serverContext.Identity = Identity;

                    // If the token is not good, disconnect.
                    if (Identity == null)
                    {
                        CacheManager.Return(cGreeting);
                        return DisconnectReason.Authentication;
                    }
                }

                var serverGreeting = _cacheManager.Rent<ServerGreetingMessage>();
                serverGreeting.Version = 0;

                await SendMessage(serverGreeting).ConfigureAwait(false);

                _cacheManager.Return(serverGreeting);
                _sessionManager!.RegisterSession(this);

                _ = Task.Factory.StartNew(InvokeOnConnected, this);

                _config.Logger?.LogTrace("ReadyTaskCompletionSource fired in server.");
                _readyTaskCompletionSource?.TrySetResult();
                CacheManager.Return(cGreeting);
                break;
            }

            case MessageType.ServerGreeting:
            {
                // Verify that this is the client
                if (IsServer)
                    return DisconnectReason.ProtocolError;

                _ = Task.Factory.StartNew(InvokeOnConnected, this);

                _config.Logger?.LogTrace("ReadyTaskCompletionSource fired in client.");
                _readyTaskCompletionSource?.TrySetResult();

                CacheManager.Return(Unsafe.As<ServerGreetingMessage>(message));
                break;

            }

            case MessageType.Invocation:
            {
                var invocationRequestMessage = message.As<InvocationMessage>();
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
                        await session._nexus.InvokeMethod(message).ConfigureAwait(false);
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
                PipeManager.UpdateState(updateStateMessage.PipeId, updateStateMessage.State);
                _cacheManager.Return(updateStateMessage);
                break;
            }

            default:
                // If we don't know what the message is, then disconnect the connection
                // as we have received invalid/unexpected data.
                return DisconnectReason.ProtocolError;
                break;
        }
        return DisconnectReason.None;
    }
}
