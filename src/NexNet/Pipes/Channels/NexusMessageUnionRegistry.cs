using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace NexNet.Pipes.Channels;

/// <summary>
/// Central registry for union message types with AOT-friendly design
/// </summary>
internal static class NexusMessageUnionRegistry<TUnion> 
    where TUnion : class, INexusPooledMessageUnion<TUnion>
{
    private static readonly FrozenDictionary<byte, UnionEntry> _unions;
    private static readonly FrozenDictionary<Type, byte> _unionsByType;
    
    static NexusMessageUnionRegistry()
    {
        var registerer = new NexusUnionBuilder();
        TUnion.RegisterMessages(registerer);
        _unions = registerer.Build();
        _unionsByType = registerer.BuildByteMap();
    }
    
    /// <summary>
    /// Rent a message by its type
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TMessage Rent<TMessage>() 
        where TMessage : class, TUnion, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
    {
        return NexusMessagePool<TMessage>.Rent();
    }
    
    /// <summary>
    /// Rent a message by its UnionId, returns as TUnion base type
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TUnion Rent(byte unionId)
    {
        if (!_unions.TryGetValue(unionId, out var factory))
        {
            throw new InvalidOperationException(
                $"UnionId {unionId} is not registered for union {typeof(TUnion).Name}");
        }
        
        return factory.Renter();
    }
    
    /// <summary>
    /// Check if a UnionId is registered
    /// </summary>
    public static bool IsRegistered(byte unionId) => _unions.ContainsKey(unionId);

    public static byte GetMessageType<T>() => _unionsByType.GetValueOrDefault(typeof(T), (byte)0);
    
    private sealed class NexusUnionBuilder : INexusUnionBuilder<TUnion>
    {
        private readonly Dictionary<byte, UnionEntry> _entries = new();
        private readonly Dictionary<Type, byte> _typeByteMap = new();
    
        public void Add<TMessage>() 
            where TMessage : class, TUnion, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
        {
            byte id = TMessage.UnionId;
            if (_entries.TryGetValue(id, out var entry))
                throw new InvalidOperationException($"UnionId {id} already registered with {entry.Type}");

            var type = typeof(TMessage);
            _typeByteMap.Add(type, id);
            _entries.Add(id, new UnionEntry(NexusMessagePool<TMessage>.Rent, type));
        }
    
        public FrozenDictionary<byte, UnionEntry> Build()
            => _entries.ToFrozenDictionary();
        
        public FrozenDictionary<Type, byte> BuildByteMap()
            => _typeByteMap.ToFrozenDictionary();
    }
    
    private record UnionEntry(Func<TUnion> Renter, Type Type);
}
