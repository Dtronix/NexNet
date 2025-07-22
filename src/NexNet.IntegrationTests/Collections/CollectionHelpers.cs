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

    public WaitForActionHandler(INexusCollection collection, NexusCollectionChangedAction action, int count = 1)
    {
        var currentCount = 0;
        _tcs = new TaskCompletionSource();
        
        _eventDisposable = collection.Changed.Subscribe(e =>
        {
            if (e.ChangedAction == action && Interlocked.Increment(ref currentCount) == count)
                _tcs.SetResult();
        });
    }
    
    public async Task WaitForActionNoTimeout(int timeout = 1)
    {
        await _tcs.Task;
    }
    
    public async Task Wait(int timeout = 1)
    {
        await _tcs.Task.Timeout(timeout);
    }


    public void Dispose()
    {
        _eventDisposable.Dispose();
    }
}
