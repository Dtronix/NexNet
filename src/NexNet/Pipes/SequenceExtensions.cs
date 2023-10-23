using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

namespace NexNet.Pipes;


/// <summary>
/// Helpers for NexNet.
/// </summary>
public static class SequenceExtensions
{

    /// <summary>
    /// Copy the <see cref="ReadOnlySequence{T}"/> to the specified Stream..
    /// </summary>
    /// <param name="source">The source <see cref="ReadOnlySequence{T}"/>.</param>
    /// <param name="destination">The destination Stream.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyTo(in this ReadOnlySequence<byte> source, Stream destination)
    {
        if (source.IsSingleSegment)
        {
            destination.Write(source.First.Span);
        }
        else
        {
            CopyToMultiSegment(source, destination);
        }
    }

    private static void CopyToMultiSegment(in ReadOnlySequence<byte> sequence, Stream destination)
    {
        var position = sequence.Start;
        while (sequence.TryGet(ref position, out var memory))
        {
            destination.Write(memory.Span);
        }
    }
}
