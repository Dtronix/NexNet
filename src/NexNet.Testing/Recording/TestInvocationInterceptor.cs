using System;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Testing.Quiescence;

namespace NexNet.Testing.Recording;

/// <summary>
/// Harness implementation of <see cref="IInvocationInterceptor"/>. Captures every incoming
/// invocation into the supplied <see cref="InvocationRecorder"/> and tracks the
/// <c>inDispatch</c> quiescence counter for the duration of the invocation.
/// </summary>
internal sealed class TestInvocationInterceptor : IInvocationInterceptor
{
    private readonly InvocationRecorder _recorder;
    private readonly QuiescenceCounters _counters;
    private readonly QuiescenceTracker _tracker;

    public TestInvocationInterceptor(
        InvocationRecorder recorder,
        QuiescenceCounters counters,
        QuiescenceTracker tracker)
    {
        _recorder = recorder;
        _counters = counters;
        _tracker = tracker;
    }

    public async ValueTask WrapAsync(InvocationMessage message, Func<ValueTask> invoke)
    {
        // Copy the args bytes eagerly. The pooled InvocationMessage will be returned to its
        // pool when dispatch completes, so we cannot retain a reference into it.
        var argsCopy = message.Arguments.ToArray();
        _recorder.Append(new InvocationRecord(message.MethodId, argsCopy, method: null));

        _counters.EnterDispatch();
        _tracker.SignalChange();
        try
        {
            await invoke().ConfigureAwait(false);
        }
        finally
        {
            _counters.ExitDispatch();
            _tracker.SignalChange();
        }
    }
}
