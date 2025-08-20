using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Internals.Collections.Lists;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Transports;

namespace NexNet.Collections;

internal abstract partial class NexusCollection
{
    private CancellationTokenSource? _relayCancellation;
    public void ConfigureRelay(INexusCollectionClientConnector relay)
    {
        if(_clientRelayConnector != null)
            throw new InvalidOperationException("Collection is already linked.");
        
        _clientRelayConnector = relay;
        ArgumentNullException.ThrowIfNull(relay);

        if (!IsServer)
            throw new InvalidOperationException("Client collections cannot be relayed");
    }


    public void StopRelay()
    {
        if (_clientRelayConnector == null)
            return;
        
        _relayCancellation?.Cancel();
    }

    public void StartRelay()
    {
        if (_clientRelayConnector == null)
            return;
        
        if (_state != NexusCollectionState.Connected)
        {
            Logger?.LogWarning("Collection is not in a disconnected state.");
            return;
        }
        
        // Check to see if a relay has been configured.
        if (_clientRelayConnector == null)
            return;
        
        _relayCancellation = new CancellationTokenSource();
        
        _relayCancellation.Token.Register(static crc =>
        {
            Unsafe.As<INexusCollectionClientConnector>(crc)!.Dispose();

        }, _clientRelayConnector);
        
        _ = Task.Factory.StartNew(async nc =>
        {
            var collection = (NexusCollection)nc!;
            while (collection._relayCancellation?.IsCancellationRequested == false)
            {
                try
                {
                    await collection.RunRelayConnectionLoop().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    collection.Logger?.LogWarning(e, "Collection failed to run relay");
                }
 
            }
            
        }, this, TaskCreationOptions.DenyChildAttach);
    }

    private async ValueTask RunRelayConnectionLoop()
    {
        if (_clientRelayConnector == null)
            return;
        
        Logger?.LogTrace("Running relay connection loop");

        INexusCollection relayConnection;
        try
        {
            relayConnection = await _clientRelayConnector.GetCollection().ConfigureAwait(false);
            Logger?.LogTrace("Retrieved relay connection");
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Failed to connect relay connection.");
            return;
        }

        if (relayConnection is not NexusCollection nexusCollection)
            throw new InvalidOperationException("Parent must be a NexusCollection");
            
        if (relayConnection.GetType() != this.GetType())
            throw new InvalidOperationException("Parent collection must be of the same type");
            
        // Set this collection as a child relay of the parent
        nexusCollection._relayTo = this;
        
        // Initialize this collection to act like it's connected to a server
        // but it will receive messages from the parent instead
        _state = NexusCollectionState.Connected;
        
        // Create a dummy disconnection task that will be completed when parent disconnects
        _disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectedTask = _disconnectTcs.Task;
        nexusCollection._disconnectedTask = _disconnectTcs.Task;
        
        // Monitor parent disconnection to trigger this collection's disconnection
        try
        {
            await nexusCollection.DisconnectedTask.ConfigureAwait(false);
            ClientDisconnected();
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error while monitoring parent collection disconnection");
            ClientDisconnected();
        }
    }
    
    /// <summary>
    /// Relays a message from a parent collection to this collection as if it came from a server
    /// </summary>
    private bool RelayMessageFromParent(INexusCollectionMessage messageFromParent)
    {
        try
        {
            // Process reset messages the same way as a client would
            switch (messageFromParent)
            {
                case NexusCollectionResetStartMessage message:
                    if (IsClientResetting)
                        return false;
                    IsClientResetting = true;
                    var startResult = OnClientResetStarted(message.Version, message.TotalValues);
                    messageFromParent.ReturnToCache();
                    return startResult;
            
                case NexusCollectionResetValuesMessage message:
                    if (!IsClientResetting)
                    {
                        messageFromParent.ReturnToCache();
                        return false;
                    }
                    var valuesResult = OnClientResetValues(message.Values.Span);
                    if (message is INexusCollectionValueMessage valueMessage)
                        valueMessage.ReturnValueToPool();
                    messageFromParent.ReturnToCache();
                    return valuesResult;

                case NexusCollectionResetCompleteMessage:
                    if (!IsClientResetting)
                    {
                        messageFromParent.ReturnToCache();
                        return false;
                    }
                    IsClientResetting = false;
                    var completeResult = OnClientResetCompleted();
                    CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Reset));
                    messageFromParent.ReturnToCache();
                    return completeResult;

                case NexusCollectionClearMessage message:
                    if (IsClientResetting)
                    {
                        messageFromParent.ReturnToCache();
                        return false;
                    }
                    var clearResult = OnClientClear(message.Version);
                    CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Reset));
                    messageFromParent.ReturnToCache();
                    return clearResult;
                    
                default:
                    if (IsClientResetting)
                    {
                        messageFromParent.ReturnToCache();
                        return false;
                    }
                    
                    // Process other messages and trigger change events
                    var processResult = OnClientProcessMessage(messageFromParent);
                    if (processResult)
                    {
                        // Trigger change event for subscribers of this relay collection
                        CoreChangedEvent.Raise(new NexusCollectionChangedEventArgs(NexusCollectionChangedAction.Reset));
                    }
                    
                    if (messageFromParent is INexusCollectionValueMessage valueMsg)
                        valueMsg.ReturnValueToPool();
                    messageFromParent.ReturnToCache();
                    
                    return processResult;
            }
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Exception while relaying message from parent");
            messageFromParent.ReturnToCache();
            return false;
        }
    }
}
