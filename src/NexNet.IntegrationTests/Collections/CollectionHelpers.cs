using NexNet.Collections;

namespace NexNet.IntegrationTests.Collections;

public static class CollectionHelpers
{
    public static WaitForActionHandler WaitForEvent(this INexusCollection collection, NexusCollectionChangedAction action, int count = 1)
    {
        return new WaitForActionHandler(collection, action, count);
    }
}

public class WaitForActionHandler : IDisposable
{
    private readonly INexusCollection _collection;
    private readonly int _targetCount;
    private readonly IDisposable _eventDisposable;
    private readonly TaskCompletionSource _tcs;
    private int _counter = 0;
    public int Counter => _counter;

    public WaitForActionHandler(INexusCollection collection, NexusCollectionChangedAction action, int targetCount = 1)
    {
        _collection = collection;
        _targetCount = targetCount;
        _tcs = new TaskCompletionSource();
        
        _eventDisposable = collection.Changed.Subscribe(e =>
        {
            if (e.ChangedAction == action && Interlocked.Increment(ref _counter) == targetCount)
                _tcs.SetResult();
        });
    }
    
    public Task WaitForActionNoTimeout(int timeout = 1)
    {
        return _tcs.Task;
    }
    
    public async Task Wait(int timeout = 1)
    {
        try
        {
            await _tcs.Task.Timeout(timeout);
        }
        catch (Exception e)
        {
            throw new Exception($"The operation timed out. Ops: {_counter}/{_targetCount}", e);
        }
        
    }


    public void Dispose()
    {
        _eventDisposable.Dispose();
    }
}
