using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Logging;

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
        {
            // Since this is not a relay and it is on the server, the collection is ready.
            _tcsReady.SetResult();
            return;
        }

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
                if(!_relayEnabled)
                    return;

                try
                {
                    var result = await collection.RunRelayConnectionLoop().ConfigureAwait(false);

                    if (result.Fatal)
                    {
                        collection.Logger?.LogError($"Collection connection failed due to fatal error. Collection connection loop exiting. {result.FatalReason} {result.FatalException}");
                        return;
                    }
                    
                    collection.Logger?.LogWarning("Collection failed to run relay");
                }
                catch (Exception e)
                {
                    collection.Logger?.LogWarning(e, "Collection failed to run relay");
                }
 
            }
            
        }, this, TaskCreationOptions.DenyChildAttach);
    }

    record struct ConnectionResult(bool Fatal, string? FatalReason = null, Exception? FatalException = null);

    private async ValueTask<ConnectionResult> RunRelayConnectionLoop()
    {
        if (_clientRelayConnector == null)
            return new ConnectionResult(true);
        
        Logger?.LogTrace("Connecting relay connection loop");

        INexusCollection relayCollection;
        try
        {
            relayCollection = await _clientRelayConnector.GetCollection().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new ConnectionResult(false, "Failed to connect relay connection.", e);
        }
        
        Logger?.LogTrace("Relay connection made.");

        if (relayCollection is not NexusCollection parentNexusCollection)
            return new ConnectionResult(true, "Source relay connection is not a NexusCollection.");
            
        if (relayCollection.GetType() != this.GetType())
            return new ConnectionResult(true, "Parent collection must be of the same type");
        
        if (parentNexusCollection.Mode != NexusCollectionMode.ServerToClient)
            return new ConnectionResult(true, "Parent collection must be in ServerToClient mode when relaying.");
        
        if (Mode != NexusCollectionMode.Relay)
            return new ConnectionResult(true, "Collection must be in Relay mode.");
            
        // Set this collection as a child relay of the parent
        parentNexusCollection._relayTo = this;
        _relayFrom = parentNexusCollection;
        try
        {
            await relayCollection.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            Logger?.LogTrace("Retrieved relay connection");
            
            // Initialize this collection to act like it's connected to a server
            // but it will receive messages from the parent instead
            _state = NexusCollectionState.Connected;
        }
        catch (Exception e)
        {
            _state = NexusCollectionState.Disconnected;
            Logger?.LogError(e, "Failed to connect relay connection.");
            return new ConnectionResult(false);
        }
        
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

        return new ConnectionResult(false);
    }
}
