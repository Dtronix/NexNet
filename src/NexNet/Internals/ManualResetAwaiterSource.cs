using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Internals;

/// <summary>
/// Manually resettable awaiter for reuse in high performance situations.
/// </summary>
internal sealed class ManualResetAwaiterSource
{
    private readonly ManualResetAwaiter _awaiter = new();
    private CancellationTokenRegistration? _cancellationTokenRegistration;

    public ITaskCompletionAwaiter Awaiter => _awaiter;

    /// <summary>
    /// Attempts to transition the completion state.
    /// </summary>
    /// <returns>True on success, false otherwise.</returns>
    public bool TrySetResult()
    {
        return _awaiter.TrySetResult();
    }

    public bool TrySetException(Exception exception)
    {
        return _awaiter.TrySetException(exception);
    }

    public bool TrySetCanceled()
    {
        return _awaiter.TrySetCanceled();
    }

    private static int _cancelCount = 0;
    public void Reset()
    {
        if (_cancellationTokenRegistration != null)
        {
            _cancellationTokenRegistration.Value.Dispose();
            _cancellationTokenRegistration = null;
        }

        _awaiter.Reset();
    }

    public void HookCancellationToken(CancellationToken token)
    {
        if (_cancellationTokenRegistration != null)
            throw new InvalidOperationException("Cancellation token already exists for operation");

        _cancellationTokenRegistration = token.Register(() =>
        {
            Console.WriteLine($"Cancellation Token Canceled {Interlocked.Increment(ref _cancelCount)} {_awaiter.TrySetCanceled()}");
            //_cancellationTokenRegistration?.Dispose();
            //_cancellationTokenRegistration = null;
        }, false);
    }

    /// <summary>
    /// Returns a task for the current awaiter state.
    /// </summary>
    /// <returns>Task for the state.</returns>
    public Task ToTask()
    {
        return Task.Run(async () => await _awaiter);
    }

    private sealed class ManualResetAwaiter : ITaskCompletionAwaiter
    {
        private Action? _continuation;
        private Exception? _exception;
        private volatile bool isInvoked = false;
        private static readonly TaskCanceledException _tce = new TaskCanceledException();
        private static int idCounter;
        private readonly int id;
        public ManualResetAwaiter()
        {
            id = Interlocked.Increment(ref idCounter);
        }
        
        public void OnCompleted(Action continuation)
        {
            //    throw new InvalidOperationException("This ReusableTaskCompletionSource instance has already been listened");

            // Check to see if the task has already been completed.
            if (IsCompleted)
            {
                Console.WriteLine($"{id} Completed Before Registration");

                if (_continuation != null)
                {
                    Console.WriteLine($"{id} Had previous registration?");
                    //_continuation.Invoke();
                }

                isInvoked = true;
                continuation.Invoke();
            }

            _continuation = continuation;
            Console.WriteLine($"{id} Registered");
        }

        public bool IsCompleted { get; private set; }
        public bool IsCanceled { get; private set; }

        public CancellationToken AttachCancellationToken { get; set; }

        public void GetResult()
        {
            if (_exception != null)
                throw _exception;
        }

        /// <summary>
        /// Attempts to transition the completion state.
        /// </summary>
        /// <returns>True on success, false otherwise.</returns>
        public bool TrySetResult()
        {
            if (IsCompleted)
                return false;

            IsCompleted = true;

            _continuation?.Invoke();
            return true;
        }

        /// <summary>
        /// Attempts to transition the exception state.
        /// </summary>
        /// <returns></returns>
        public bool TrySetException(Exception exception)
        {
            if (IsCompleted)
                return false;

            IsCompleted = true;
            _exception = exception;

            _continuation?.Invoke();
            return true;
        }

        /// <summary>
        /// Attempts to transition to the canceled state.
        /// </summary>
        /// <returns></returns>
        public bool TrySetCanceled()
        {
            if (IsCompleted || isInvoked)
                return false;

            isInvoked = true;
            Console.WriteLine($"{id} Canceled");
            IsCompleted = true;
            IsCanceled = true;
            _exception = _tce;

            _continuation?.Invoke();
            return true;
        }

        /// <summary>
        /// Reset the awaiter to initial status
        /// </summary>
        /// <returns></returns>
        public void Reset()
        {
            Console.WriteLine($"{id} Reset");
            _continuation = null;
            _exception = null;
            IsCompleted = false;
            IsCanceled = false;
            isInvoked = false;
        }
        public ITaskCompletionAwaiter GetAwaiter()
        {
            return this;
        }
    }
}
