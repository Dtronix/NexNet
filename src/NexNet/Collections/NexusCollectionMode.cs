namespace NexNet.Collections;

/// <summary>
/// Specifies the synchronization mode of a Nexus-backed collection.
/// </summary>
public enum NexusCollectionMode
{
    /// <summary>
    /// The mode has not been explicitly set.  
    /// Collections in this state will not synchronize until a mode is chosen.
    /// </summary>
    Unset,

    /// <summary>
    /// One-way synchronization from the server to the client.  
    /// Changes made on the server are pushed to connected clients, 
    /// but client-side mutations are not sent back to the server.
    /// </summary>
    ServerToClient,

    /// <summary>
    /// Two-way (bi-directional) synchronization.  
    /// Changes on either the server or any connected client are propagated
    /// to all other participants.
    /// </summary>
    BiDirectional,


    /// <summary>
    /// Collection is configured to be a relay and won't accept any updates.
    /// </summary>
    Relay
}
