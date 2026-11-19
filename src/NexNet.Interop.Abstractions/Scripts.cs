using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexNet.Interop;

/// <summary>The error kinds shared by every language of the harness (each maps to a C# exception type).</summary>
public enum ErrorKind : byte
{
    /// <summary>The transport failed or was refused.</summary>
    Transport,

    /// <summary>The remote method threw.</summary>
    RemoteInvocation,

    /// <summary>The call was not authorized.</summary>
    Unauthorized,

    /// <summary>The call was canceled.</summary>
    Canceled,

    /// <summary>The session disconnected.</summary>
    Disconnected,

    /// <summary>A value could not be serialized or deserialized.</summary>
    Serialization,

    /// <summary>The peer is busy (no invocation credit).</summary>
    Busy,

    /// <summary>A message exceeded the size limit.</summary>
    TooLarge,

    /// <summary>The operation is invalid in the current state.</summary>
    InvalidOperation,
}

/// <summary>One step of a script slot, run by the language host when the server invokes the method.</summary>
public abstract record ScriptStep;

/// <summary>Completes the call with <paramref name="Value"/> (typed JSON; null for methods without a result).</summary>
/// <param name="Value">The result.</param>
public sealed record ReturnStep(JsonNode? Value) : ScriptStep;

/// <summary>Fails the call.</summary>
/// <param name="Kind">The error kind.</param>
/// <param name="Message">The error message.</param>
public sealed record ThrowStep(ErrorKind Kind, string Message) : ScriptStep;

/// <summary>Waits inside the host.</summary>
/// <param name="Milliseconds">The delay.</param>
public sealed record DelayStep(int Milliseconds) : ScriptStep;

/// <summary>Waits until the test raises the named signal.</summary>
/// <param name="Name">The signal name.</param>
public sealed record AwaitSignalStep(string Name) : ScriptStep;

/// <summary>Waits until the call's cancellation token is canceled.</summary>
public sealed record AwaitCancelStep : ScriptStep;

/// <summary>Invokes a server method from inside the handler.</summary>
/// <param name="Method">The server method.</param>
/// <param name="Arguments">Its arguments, as typed JSON in wire order.</param>
public sealed record InvokeStep(RemoteMethod Method, JsonArray Arguments) : ScriptStep;

/// <summary>Runs a handler built into every host (e.g. <c>pipe.echo</c>, <c>pipe.drain</c>, <c>channel.pump</c>, <c>channel.echo</c>).</summary>
/// <param name="Handler">The handler name.</param>
/// <param name="Options">Handler options, or null.</param>
public sealed record NamedStep(string Handler, JsonObject? Options) : ScriptStep;

/// <summary>Round-trips the call to the C# lambda of <see cref="ScriptSlot{TArgs, TResult}.Forward(Func{TArgs, CancellationToken, ValueTask{TResult}})"/>.</summary>
public sealed record ForwardStep : ScriptStep;

/// <summary>Encodes the arguments record of a script slot to and from typed JSON (wire order).</summary>
/// <typeparam name="TArgs">The arguments record.</typeparam>
public sealed class ScriptArgsCodec<TArgs>
{
    private readonly Func<JsonArray, TArgs> _decode;
    private readonly Func<TArgs, JsonArray> _encode;

    /// <summary>Creates a codec.</summary>
    public ScriptArgsCodec(Func<JsonArray, TArgs> decode, Func<TArgs, JsonArray> encode)
    {
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        _encode = encode ?? throw new ArgumentNullException(nameof(encode));
    }

    /// <summary>The arguments of typed-JSON <paramref name="arguments"/>.</summary>
    public TArgs Decode(JsonArray arguments) => _decode(arguments ?? throw new ArgumentNullException(nameof(arguments)));

    /// <summary>The typed JSON of <paramref name="arguments"/>.</summary>
    public JsonArray Encode(TArgs arguments) => _encode(arguments);
}

/// <summary>The arguments of a method with no serialized parameters.</summary>
public sealed record NoArgs
{
    private NoArgs()
    {
    }

    /// <summary>The only value.</summary>
    public static NoArgs Value { get; } = new();

    /// <summary>The codec: an empty array.</summary>
    public static ScriptArgsCodec<NoArgs> Codec { get; } = new(static _ => Value, static _ => new JsonArray());
}

/// <summary>The result of a method that returns no value.</summary>
public readonly record struct NoResult
{
    /// <summary>The only value.</summary>
    public static NoResult Value => default;
}

/// <summary>The arguments of the <see cref="ScriptFacade.OnConnected"/> slot.</summary>
/// <param name="IsReconnected">True when the client reconnected.</param>
public sealed record ConnectedArgs(bool IsReconnected)
{
    /// <summary>The codec: <c>[isReconnected]</c>.</summary>
    public static ScriptArgsCodec<ConnectedArgs> Codec { get; } = new(
        static a => new ConnectedArgs(a.Count > 0 && a[0] is { } value && value.GetValue<bool>()),
        static v => new JsonArray(JsonValue.Create(v.IsReconnected)));
}

/// <summary>
/// The script of one client-nexus method (or lifecycle event): what the client under test does when the server calls
/// it. The harness drivers read <see cref="Steps"/> and report calls back; tests use the typed
/// <see cref="ScriptSlot{TArgs, TResult}"/>.
/// </summary>
public abstract class ScriptSlot
{
    private readonly object _gate = new();
    private readonly List<ScriptStep> _steps = new();
    private int? _times;

    private protected ScriptSlot(string name, RemoteMethod? method)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Method = method;
    }

    /// <summary>The method name, or <c>OnConnected</c>/<c>OnDisconnected</c>.</summary>
    public string Name { get; }

    /// <summary>The client method; null for a lifecycle slot.</summary>
    public RemoteMethod? Method { get; }

    /// <summary>The steps, in order. Empty: the method completes with its default result.</summary>
    public IReadOnlyList<ScriptStep> Steps
    {
        get
        {
            lock (_gate)
                return _steps.ToArray();
        }
    }

    /// <summary>How many calls the steps apply to; null for every call.</summary>
    public int? TimesLimit
    {
        get
        {
            lock (_gate)
                return _times;
        }
    }

    /// <summary>Raised after the steps or the limit change, so a driver can resend the script.</summary>
    public event EventHandler? Changed;

    /// <summary>Removes every step and the limit.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _steps.Clear();
            _times = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs the <see cref="ForwardStep"/> lambda for a call with typed-JSON arguments; returns the typed-JSON result.</summary>
    /// <exception cref="InvalidOperationException">The slot has no forward lambda.</exception>
    public abstract ValueTask<JsonNode?> ForwardAsync(JsonArray arguments, CancellationToken cancellationToken);

    /// <summary>Reports a call the client received, with typed-JSON arguments (observed by <c>NextCallAsync</c>).</summary>
    public abstract void ReportCall(JsonArray arguments);

    private protected void AddStep(ScriptStep step)
    {
        lock (_gate)
            _steps.Add(step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private protected void SetTimes(int times)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(times);
        lock (_gate)
            _times = times;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The typed script of one client-nexus method.</summary>
/// <typeparam name="TArgs">The generated arguments record (or <see cref="NoArgs"/>).</typeparam>
/// <typeparam name="TResult">The result type (or <see cref="NoResult"/>).</typeparam>
public sealed class ScriptSlot<TArgs, TResult> : ScriptSlot
{
    private readonly ScriptArgsCodec<TArgs> _args;
    private readonly TypedCodec<TResult>? _result;
    private readonly Channel<TArgs> _calls = Channel.CreateUnbounded<TArgs>();
    private Func<TArgs, CancellationToken, ValueTask<TResult>>? _forward;

    /// <summary>Creates a slot.</summary>
    /// <param name="name">The method name.</param>
    /// <param name="method">The client method; null for a lifecycle slot.</param>
    /// <param name="args">The arguments codec.</param>
    /// <param name="result">The result codec; null when the method returns no value.</param>
    public ScriptSlot(string name, RemoteMethod? method, ScriptArgsCodec<TArgs> args, TypedCodec<TResult>? result)
        : base(name, method)
    {
        _args = args ?? throw new ArgumentNullException(nameof(args));
        _result = result;
    }

    /// <summary>The arguments codec.</summary>
    public ScriptArgsCodec<TArgs> ArgsCodec => _args;

    /// <summary>Completes the call with <paramref name="value"/>.</summary>
    public ScriptSlot<TArgs, TResult> Returns(TResult value) => Step(new ReturnStep(_result?.Encode(value)));

    /// <summary>Fails the call.</summary>
    public ScriptSlot<TArgs, TResult> Throws(ErrorKind kind, string message) => Step(new ThrowStep(kind, message ?? ""));

    /// <summary>Waits <paramref name="milliseconds"/> inside the host.</summary>
    public ScriptSlot<TArgs, TResult> Delay(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        return Step(new DelayStep(milliseconds));
    }

    /// <summary>Waits <paramref name="delay"/> inside the host.</summary>
    public ScriptSlot<TArgs, TResult> Delay(TimeSpan delay) => Delay(checked((int)delay.TotalMilliseconds));

    /// <summary>Waits until the test raises signal <paramref name="name"/>.</summary>
    public ScriptSlot<TArgs, TResult> AwaitSignal(string name) => Step(new AwaitSignalStep(name ?? throw new ArgumentNullException(nameof(name))));

    /// <summary>Waits until the call is canceled.</summary>
    public ScriptSlot<TArgs, TResult> AwaitCancel() => Step(new AwaitCancelStep());

    /// <summary>Invokes server method <paramref name="method"/> from inside the handler.</summary>
    public ScriptSlot<TArgs, TResult> Invoke(RemoteMethod method, JsonArray arguments)
        => Step(new InvokeStep(method ?? throw new ArgumentNullException(nameof(method)), arguments ?? new JsonArray()));

    /// <summary>Runs the host's built-in handler <paramref name="handler"/>.</summary>
    public ScriptSlot<TArgs, TResult> Named(string handler, JsonObject? options = null)
        => Step(new NamedStep(handler ?? throw new ArgumentNullException(nameof(handler)), options));

    /// <summary>Round-trips each call to <paramref name="handler"/> in the test process.</summary>
    public ScriptSlot<TArgs, TResult> Forward(Func<TArgs, CancellationToken, ValueTask<TResult>> handler)
    {
        _forward = handler ?? throw new ArgumentNullException(nameof(handler));
        return Step(new ForwardStep());
    }

    /// <summary>Round-trips each call to <paramref name="handler"/> in the test process.</summary>
    public ScriptSlot<TArgs, TResult> Forward(Func<TArgs, ValueTask<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Forward((args, _) => handler(args));
    }

    /// <summary>Applies the steps to the next <paramref name="times"/> calls only.</summary>
    public ScriptSlot<TArgs, TResult> Times(int times)
    {
        SetTimes(times);
        return this;
    }

    /// <summary>The arguments of the next call the client receives (in arrival order).</summary>
    public ValueTask<TArgs> NextCallAsync(CancellationToken cancellationToken = default) => _calls.Reader.ReadAsync(cancellationToken);

    /// <inheritdoc />
    public override void ReportCall(JsonArray arguments) => ReportCall(_args.Decode(arguments));

    /// <summary>Reports a call the client received.</summary>
    public void ReportCall(TArgs arguments) => _calls.Writer.TryWrite(arguments);

    /// <inheritdoc />
    public override async ValueTask<JsonNode?> ForwardAsync(JsonArray arguments, CancellationToken cancellationToken)
    {
        var forward = _forward ?? throw new InvalidOperationException("The script slot '" + Name + "' has no Forward handler.");
        var result = await forward(_args.Decode(arguments), cancellationToken).ConfigureAwait(false);
        return _result?.Encode(result);
    }

    private ScriptSlot<TArgs, TResult> Step(ScriptStep step)
    {
        AddStep(step);
        return this;
    }
}

/// <summary>
/// Base of a generated script facade (<c>&lt;ClientNexus&gt;Script</c>): one slot per client-nexus method plus the
/// <see cref="OnConnected"/> and <see cref="OnDisconnected"/> lifecycle slots.
/// </summary>
public abstract class ScriptFacade
{
    private readonly List<ScriptSlot> _slots = new();

    /// <summary>Creates the facade of a client nexus interface.</summary>
    /// <param name="clientInterface">Full metadata name of the client nexus interface.</param>
    protected ScriptFacade(string clientInterface)
    {
        ClientInterface = clientInterface ?? throw new ArgumentNullException(nameof(clientInterface));
        OnConnected = Add(new ScriptSlot<ConnectedArgs, NoResult>("OnConnected", null, ConnectedArgs.Codec, null));
        OnDisconnected = Add(new ScriptSlot<NoArgs, NoResult>("OnDisconnected", null, NoArgs.Codec, null));
    }

    /// <summary>Full metadata name of the client nexus interface.</summary>
    public string ClientInterface { get; }

    /// <summary>Runs when the client connects or reconnects.</summary>
    public ScriptSlot<ConnectedArgs, NoResult> OnConnected { get; }

    /// <summary>Runs when the client disconnects.</summary>
    public ScriptSlot<NoArgs, NoResult> OnDisconnected { get; }

    /// <summary>Every slot: the lifecycle slots, then the methods by id.</summary>
    public IReadOnlyList<ScriptSlot> Slots => _slots;

    /// <summary>The slot named <paramref name="name"/>, or null.</summary>
    public ScriptSlot? Find(string name)
    {
        foreach (var slot in _slots)
        {
            if (string.Equals(slot.Name, name, StringComparison.Ordinal))
                return slot;
        }

        return null;
    }

    /// <summary>The slot of the client method with wire id <paramref name="id"/>, or null.</summary>
    public ScriptSlot? Find(ushort id)
    {
        foreach (var slot in _slots)
        {
            if (slot.Method != null && slot.Method.Id == id)
                return slot;
        }

        return null;
    }

    /// <summary>Adds a slot (generated constructors call this in method id order).</summary>
    protected TSlot Add<TSlot>(TSlot slot)
        where TSlot : ScriptSlot
    {
        ArgumentNullException.ThrowIfNull(slot);
        _slots.Add(slot);
        return slot;
    }
}
