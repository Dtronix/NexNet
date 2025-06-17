using System;
using System.Collections.Generic;

namespace NexNet.Internals;

internal static class IEnumerableExtensions
{
    public static IEnumerable<Memory<T>> MemoryChunk<T>(this IEnumerable<T> source, int chunkSize)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (chunkSize <= 0) throw new ArgumentException("Chunk size must be positive.", nameof(chunkSize));

        T[] buffer = new T[chunkSize]; // Single buffer reused for all chunks
        int index = 0;

        foreach (var item in source)
        {
            buffer[index++] = item;
            if (index == chunkSize)
            {
                // Yield the full span of buffer
                yield return buffer.AsMemory().Slice(0, chunkSize);
                index = 0; // Reset buffer for next chunk
            }
        }

        if (index > 0)
        {
            // Yield the remaining elements in the buffer
            yield return buffer.AsMemory().Slice(0, index);
        }
    }
}
