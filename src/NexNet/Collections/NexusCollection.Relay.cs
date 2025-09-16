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
    private bool _relayEnabled;

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

        _relayEnabled = false;
        _ = _relayFrom?.DisconnectAsync();
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
        
        _relayEnabled = true;
        
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
                    if(!_relayEnabled)
                        return;
                    
                    collection.Logger?.LogWarning("Collection failed to run relay");
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
        
        Logger?.LogTrace("Connecting relay connection loop");

        INexusCollection relayConnection;
        try
        {
            relayConnection = await _clientRelayConnector.GetCollection().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Failed to connect relay connection.");
            return;
        }
        
        Logger?.LogTrace("Relay connection made.");

        if (relayConnection is not NexusCollection parentNexusCollection)
            throw new InvalidOperationException("Parent must be a NexusCollection");
            
        if (relayConnection.GetType() != this.GetType())
            throw new InvalidOperationException("Parent collection must be of the same type");
            
        // Set this collection as a child relay of the parent
        parentNexusCollection._relayTo = this;
        _relayFrom = parentNexusCollection;
        try
        {
            await relayConnection.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            Logger?.LogTrace("Retrieved relay connection");
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Failed to connect relay connection.");
            return;
        }
        
        // Initialize this collection to act like it's connected to a server
        // but it will receive messages from the parent instead
        _state = NexusCollectionState.Connected;
        
        // Monitor parent disconnection to trigger this collection's disconnection
        try
        {
            await parentNexusCollection._disconnectedTask.ConfigureAwait(false);
            ClientDisconnected();
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error while monitoring parent collection disconnection");
            ClientDisconnected();
        }
    }
}
