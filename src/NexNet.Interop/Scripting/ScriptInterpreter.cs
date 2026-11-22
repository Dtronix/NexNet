using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace NexNet.Interop.Scripting;

/// <summary>One call of a client-nexus method (or lifecycle event) that a script handles.</summary>
/// <param name="Slot">The slot of the method.</param>
/// <param name="Arguments">The serialized arguments, as typed JSON in wire order.</param>
/// <param name="CancellationToken">The call's cancellation token (canceled when the server cancels it).</param>
/// <param name="Pipe">The <c>INexusDuplexPipe</c> or <c>INexusDuplexChannel&lt;T&gt;</c> argument, or null.</param>
public sealed record ScriptCall(ScriptSlot Slot, JsonArray Arguments, CancellationToken CancellationToken, object? Pipe);

/// <summary>Runs a <see cref="NamedStep"/>; returns a typed-JSON result or null.</summary>
public delegate ValueTask<JsonNode?> NamedHandler(NamedStep step, ScriptCall call);

/// <summary>
/// Runs the steps of a <see cref="ScriptSlot"/> for one call. This is the reference semantics every language host
/// implements; the in-process C# driver uses this class itself.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The call is reported to the slot (<see cref="ScriptSlot.ReportCall"/>) before any step runs.</item>
/// <item>When the slot has a <see cref="ScriptSlot.TimesLimit"/>, its steps apply to that many calls after the last
/// change of the slot; later calls run no steps.</item>
/// <item>Steps run in order. <see cref="ReturnStep"/> and <see cref="ForwardStep"/> complete the call with their result,
/// <see cref="ThrowStep"/> fails it with the C# exception of its <see cref="ErrorKind"/>. The other steps continue:
/// <see cref="DelayStep"/> and <see cref="AwaitSignalStep"/> wait (and fail with a cancellation when the call is
/// canceled), <see cref="AwaitCancelStep"/> waits until the call is canceled, <see cref="InvokeStep"/> invokes the
/// server method and waits for it, <see cref="NamedStep"/> runs a built-in handler.</item>
/// <item>A call whose steps end without completing it returns the default result (typed JSON <c>null</c>).</item>
/// </list>
/// </remarks>
public sealed class ScriptInterpreter
{
    private sealed class SlotState
    {
        public int Calls;
    }

    private readonly ConditionalWeakTable<ScriptSlot, SlotState> _states = new();
    private readonly ScriptSignals _signals;
    private readonly IRemoteInvoker _invoker;
    private readonly NamedHandler _named;

    /// <summary>Creates an interpreter.</summary>
    /// <param name="signals">The signals <see cref="AwaitSignalStep"/> waits for.</param>
    /// <param name="invoker">Runs <see cref="InvokeStep"/> server invocations.</param>
    /// <param name="named">Runs <see cref="NamedStep"/> built-in handlers.</param>
    public ScriptInterpreter(ScriptSignals signals, IRemoteInvoker invoker, NamedHandler named)
    {
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _named = named ?? throw new ArgumentNullException(nameof(named));
    }

    /// <summary>Runs the script of <paramref name="call"/> and returns the typed-JSON result.</summary>
    public async ValueTask<JsonNode?> RunAsync(ScriptCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var slot = call.Slot;
        slot.ReportCall((JsonArray)call.Arguments.DeepClone());

        var steps = slot.Steps;
        if (slot.TimesLimit is { } limit)
        {
            var state = _states.GetValue(slot, s =>
            {
                var created = new SlotState();
                s.Changed += (_, _) => Volatile.Write(ref created.Calls, 0);
                return created;
            });

            if (Interlocked.Increment(ref state.Calls) > limit)
                steps = [];
        }

        var token = call.CancellationToken;
        foreach (var step in steps)
        {
            switch (step)
            {
                case ReturnStep r:
                    return r.Value?.DeepClone();
                case ThrowStep t:
                    throw ErrorKinds.ToException(t.Kind, t.Message);
                case ForwardStep:
                    return await slot.ForwardAsync((JsonArray)call.Arguments.DeepClone(), token).ConfigureAwait(false);
                case DelayStep d:
                    await Task.Delay(d.Milliseconds, token).ConfigureAwait(false);
                    break;
                case AwaitSignalStep s:
                    await _signals.WaitAsync(s.Name, token).ConfigureAwait(false);
                    break;
                case AwaitCancelStep:
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }

                    break;
                case InvokeStep i:
                    await InvokeAsync(i).ConfigureAwait(false);
                    break;
                case NamedStep n:
                    await _named(n, call).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException("Unknown script step " + step.GetType().Name + ".");
            }
        }

        return null;
    }

    private async ValueTask InvokeAsync(InvokeStep step)
    {
        var invocation = new RemoteInvocation(step.Method, (JsonArray)step.Arguments.DeepClone(), RemoteCancel.None, null);
        switch (step.Method.ReturnKind)
        {
            case RemoteReturnKind.Void:
                _invoker.Invoke(invocation);
                break;
            case RemoteReturnKind.Task:
                await _invoker.InvokeAsync(invocation).ConfigureAwait(false);
                break;
            default:
                await _invoker.InvokeWithResultAsync(invocation).ConfigureAwait(false);
                break;
        }
    }
}
