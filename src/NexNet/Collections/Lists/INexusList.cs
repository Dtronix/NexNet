using System.Threading.Tasks;

namespace NexNet.Collections.Lists;

public interface INexusList<T> : INexusCollection
{
    ValueTask ClearAsync();
    bool Contains(T item);
    void CopyTo(T[] array, int arrayIndex);
    
    ValueTask<bool> RemoveAsync(T item);
    int Count { get; }
    bool IsReadOnly { get; }
    int IndexOf(T item);
    ValueTask InsertAsync(int index, T item);
    ValueTask RemoveAtAsync(int index);
    T this[int index] { get; }


    ValueTask AddAsync(T item);
}
