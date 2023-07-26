using System;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace NexNet.Messages;

internal interface IMessageBase
{
    public static abstract MessageType Type { get; }
}

internal static class MessageBaseExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T As<T>(this IMessageBase message)
        where T : class, IMessageBase
    {
        return Unsafe.As<T>(message);
    }
}
