using System;
using System.Threading.Tasks;

namespace NexNet.Internals.Tasks;

/// <summary>
/// Manually resettable awaiter for reuse in high performance situations.
/// </summary>
internal sealed class ResetAwaiterSource : IManualResetAwaiterSource
{
    private readonly ResetAwaiter _awaiter;

    public ITaskCompletionAwaiter Awaiter => _awaiter;

    public ResetAwaiterSource(bool autoReset = false)
    {
        _awaiter = new ResetAwaiter(autoReset);
    }

    /// <summary>
    /// Attempts to transition the completion state.
    /// </summary>
    /// <returns>True on success, false otherwise.</returns>
    public bool TrySetResult()
    {
        return _awaiter.TrySetResult();
    }

    /// <inheritdoc />
    public bool TrySetException(Exception exception)
    {
        return _awaiter.TrySetException(exception);
    }

    /// <inheritdoc />
    public bool TrySetCanceled()
    {
        return _awaiter.TrySetCanceled();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _awaiter.Reset();
    }

    /// <summary>
    /// Returns a task for the current awaiter state.
    /// </summary>
    /// <returns>Task for the state.</returns>
    public Task ToTask()
    {
        return Task.Run(async () => await _awaiter);
    }


    private sealed class ResetAwaiter : ITaskCompletionAwaiter
    {
        private readonly bool _autoReset;
        private Action? _continuation;
        private Exception? _exception;

        public ResetAwaiter(bool autoReset)
        {
            _autoReset = autoReset;
        }

        public void OnCompleted(Action continuation)
        {
            if (_continuation != null)
                throw new InvalidOperationException("This ReusableTaskCompletionSource instance has already been listened");
            _continuation = continuation;
        }

        public bool IsCompleted { get; private set; }

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

            if (_autoReset)
                Reset();

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

            if (_autoReset)
                Reset();

            return true;
        }

        /// <summary>
        /// Attempts to transition to the canceled state.
        /// </summary>
        /// <returns></returns>
        public bool TrySetCanceled()
        {
            if (IsCompleted)
                return false;

            IsCompleted = true;
            _exception = new OperationCanceledException();

            _continuation?.Invoke();

            if (_autoReset)
                Reset();

            return true;
        }

        /// <summary>
        /// Reset the awaiter to initial status
        /// </summary>
        /// <returns></returns>
        public void Reset()
        {
            _continuation = null;
            _exception = null;
            IsCompleted = false;
        }
        public ITaskCompletionAwaiter GetAwaiter()
        {
            return this;
        }
    }
}
