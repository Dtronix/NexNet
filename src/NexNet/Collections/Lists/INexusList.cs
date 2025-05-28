using System.Threading.Tasks;

namespace NexNet.Collections.Lists;

public interface INexusList<T> : INexusCollection
{
    ValueTask Clear();
    bool Contains(T item);
    void CopyTo(T[] array, int arrayIndex);
    
    ValueTask<bool> Remove(T item);
    int Count { get; }
    bool IsReadOnly { get; }
    int IndexOf(T item);
    ValueTask Insert(int index, T item);
    ValueTask RemoveAt(int index);
    T this[int index] { get; set; }

    
}
