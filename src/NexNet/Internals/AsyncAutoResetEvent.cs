using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals.Tasks;

namespace NexNet.Internals;
internal class AsyncAutoResetEvent
{
    private readonly ManualResetAwaiter _wait = new ManualResetAwaiter();
    private readonly object _lock = new object();

    //private readonly Queue<TaskCompletionSource<bool>> m_waits = new Queue<TaskCompletionSource<bool>>();
    private bool _signaled;
    private bool _waiting;

    public bool IsSignaled => _signaled;

    private record CancellationCallbackArgs(CancellationTokenSource CancellationTokenSource);

    public AsyncAutoResetEvent(bool initiallySignaled)
    {
        _signaled = initiallySignaled;
    }


    private static void CancellationCallback(object? argsObject)
    {
        var args = Unsafe.As<ManualResetAwaiterSource>(argsObject)!;
        args.TrySetCanceled();
    }



    public async ValueTask WaitAsync(CancellationToken token = default)
    {
        //Console.WriteLine("WaitAsync");
        bool wait = true;
        lock (_lock)
        {
            if (_waiting)
                throw new InvalidOperationException("Multiple waits not allowed");

            if (_signaled)
            {
                //Console.WriteLine("Signaled Hot Path");
                wait = false;
                _signaled = false;
                _wait.Reset();
                return;
            }
            else
            {
                //Console.WriteLine("Signaled Normal Path");
                wait = true;
                _waiting = true;
            }
        }

        if (wait)
        {
            if (token.IsCancellationRequested)
            {
                _wait.Reset();
                return;
            }

            CancellationTokenRegistration? ctr = null;

            if (token.CanBeCanceled)
                ctr = token.UnsafeRegister(CancellationCallback, _wait);

            try
            {
                await _wait;
            }
            finally
            {
                _wait.Reset();
                _signaled = false;

                if (ctr != null)
                    await ctr.Value.DisposeAsync();

                _waiting = false;
            }

        }
    }

    public void Set()
    {
        //Console.WriteLine($"Signal Issued: {_signaled}");
        lock (_lock)
        {
            _signaled = true;
        }

        //Console.WriteLine("Signaled");
        _wait.TrySetResult();
    }
}
