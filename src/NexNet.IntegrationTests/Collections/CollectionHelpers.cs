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
    private readonly IDisposable _eventDisposable;
    private readonly TaskCompletionSource _tcs;
    private int _counter = 0;
    public int Counter => _counter;

    public WaitForActionHandler(INexusCollection collection, NexusCollectionChangedAction action, int count = 1)
    {
        _tcs = new TaskCompletionSource();
        
        _eventDisposable = collection.Changed.Subscribe(e =>
        {
            if (e.ChangedAction == action && Interlocked.Increment(ref _counter) == count)
                _tcs.SetResult();
        });
    }
    
    public Task WaitForActionNoTimeout(int timeout = 1)
    {
        return _tcs.Task;
    }
    
    public Task Wait(int timeout = 1)
    {
        return _tcs.Task.Timeout(timeout);
    }


    public void Dispose()
    {
        _eventDisposable.Dispose();
    }
}
