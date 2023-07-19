using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace NexNet;


/// <summary>
/// Helpers for NexNet.
/// </summary>
public static class Helpers
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
        SequencePosition position = sequence.Start;
        while (sequence.TryGet(ref position, out ReadOnlyMemory<byte> memory))
        {
            destination.Write(memory.Span);
        }
    }
}
