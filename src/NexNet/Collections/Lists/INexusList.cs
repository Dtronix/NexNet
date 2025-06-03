using System.Threading.Tasks;

namespace NexNet.Collections.Lists;

public interface INexusList<T> : INexusCollection
{
    Task<bool> ClearAsync();
    bool Contains(T item);
    void CopyTo(T[] array, int arrayIndex);
    int Count { get; }
    bool IsReadOnly { get; }
    int IndexOf(T item);
    Task<bool> InsertAsync(int index, T item);
    Task<bool> RemoveAtAsync(int index);
    Task<bool> RemoveAsync(T item);
    Task<bool> AddAsync(T item);
    T this[int index] { get; }
}
