using System.Threading.Tasks;

namespace NexNet.Collections.Lists;

public interface INexusList<T> : INexusCollection
{
    Task ClearAsync();
    bool Contains(T item);
    void CopyTo(T[] array, int arrayIndex);
    
    Task<bool> RemoveAsync(T item);
    int Count { get; }
    bool IsReadOnly { get; }
    int IndexOf(T item);
    Task InsertAsync(int index, T item);
    Task RemoveAtAsync(int index);
    T this[int index] { get; }


    Task AddAsync(T item);
}
