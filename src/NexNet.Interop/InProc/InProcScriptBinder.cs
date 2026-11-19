using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using NexNet.Interop.Reflection;
using NexNet.Interop.Scripting;
using NexNet.Pipes;

namespace NexNet.Interop.InProc;

/// <summary>
/// Connects the slots of a script facade to the handler hooks of a C# client nexus, so the
/// <see cref="ScriptInterpreter"/> decides what each client-nexus method does.
/// </summary>
/// <remarks>
/// For the slot of method <c>M</c>, the hook is a public delegate field or property of the nexus named <c>MEvent</c>
/// (whose first parameter may be the nexus itself, as in the integration tests' <c>ClientNexus</c>) or <c>MHandler</c>
/// (as in <c>InteropClientNexus</c>). Lifecycle slots use <c>OnConnectedEvent</c>/<c>OnConnectedHandler</c> and
/// <c>OnDisconnectedEvent</c>/<c>OnDisconnectedHandler</c> when the nexus has them; otherwise the driver runs them from
/// the client's state changes. Adding steps to a method slot that has no hook throws.
/// </remarks>
internal sealed class InProcScriptBinder
{
    private static readonly MethodInfo RunVoidMethod = typeof(BoundSlot).GetMethod(nameof(BoundSlot.RunVoid))!;
    private static readonly MethodInfo RunTaskMethod = typeof(BoundSlot).GetMethod(nameof(BoundSlot.RunTask))!;
    private static readonly MethodInfo RunTaskOfMethod = typeof(BoundSlot).GetMethod(nameof(BoundSlot.RunTaskOf))!;

    private readonly ScriptInterpreter _interpreter;
    private readonly Func<CancellationToken> _lifetime;

    public InProcScriptBinder(ScriptInterpreter interpreter, Func<CancellationToken> lifetime)
    {
        _interpreter = interpreter;
        _lifetime = lifetime;
    }

    /// <summary>Binds every slot of <paramref name="facade"/> to <paramref name="nexus"/>; returns the lifecycle slots left unbound.</summary>
    public IReadOnlyList<ScriptSlot> Bind(ScriptFacade facade, object nexus)
    {
        var unbound = new List<ScriptSlot>();
        foreach (var slot in facade.Slots)
        {
            var hook = FindHook(nexus.GetType(), slot.Name);
            if (hook == null)
            {
                if (slot.Method == null)
                {
                    unbound.Add(slot);
                }
                else
                {
                    slot.Changed += (_, _) =>
                    {
                        if (slot.Steps.Count > 0)
                            throw new NotSupportedException(
                                $"{nexus.GetType()} has no {slot.Name}Event or {slot.Name}Handler hook, so the C# client cannot script {slot.Name}.");
                    };
                }

                continue;
            }

            hook.SetValue(nexus, CreateDelegate(hook.DelegateType, new BoundSlot(this, slot, nexus.GetType(), hook.DelegateType)));
        }

        return unbound;
    }

    /// <summary>Runs a lifecycle slot that has no hook (fire and forget).</summary>
    public void RunDetached(ScriptSlot slot, JsonArray arguments)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _interpreter.RunAsync(new ScriptCall(slot, arguments, _lifetime(), null)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A lifecycle script has nobody to report to.
            }
        });
    }

    private sealed record Hook(Type DelegateType, Action<object, object?> SetValue);

    private static Hook? FindHook(Type nexusType, string name)
    {
        foreach (var memberName in new[] { name + "Event", name + "Handler" })
        {
            if (nexusType.GetField(memberName, BindingFlags.Public | BindingFlags.Instance) is { } field
                && typeof(Delegate).IsAssignableFrom(field.FieldType))
                return new Hook(field.FieldType, field.SetValue);

            if (nexusType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true } property
                && typeof(Delegate).IsAssignableFrom(property.PropertyType))
                return new Hook(property.PropertyType, property.SetValue);
        }

        return null;
    }

    private static Delegate CreateDelegate(Type delegateType, BoundSlot bound)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var arguments = Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object))));

        var returnType = invoke.ReturnType;
        MethodInfo run;
        if (returnType == typeof(void))
            run = RunVoidMethod;
        else if (returnType == typeof(ValueTask))
            run = RunTaskMethod;
        else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            run = RunTaskOfMethod.MakeGenericMethod(returnType.GetGenericArguments()[0]);
        else
            throw new NotSupportedException($"Cannot script a hook of type {delegateType}.");

        var body = Expression.Call(Expression.Constant(bound), run, arguments);
        return Expression.Lambda(delegateType, body, parameters).Compile();
    }

    /// <summary>A slot bound to a hook: turns the hook's CLR arguments into a <see cref="ScriptCall"/>.</summary>
    public sealed class BoundSlot
    {
        private readonly InProcScriptBinder _binder;
        private readonly ScriptSlot _slot;
        private readonly Type[] _parameterTypes;
        private readonly Type _nexusType;

        internal BoundSlot(InProcScriptBinder binder, ScriptSlot slot, Type nexusType, Type delegateType)
        {
            _binder = binder;
            _slot = slot;
            _nexusType = nexusType;
            _parameterTypes = delegateType.GetMethod("Invoke")!.GetParameters().Select(p => p.ParameterType).ToArray();
        }

        public void RunVoid(object?[] arguments)
        {
            var call = CreateCall(arguments);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _binder._interpreter.RunAsync(call).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Fire-and-forget methods have no caller to report to.
                }
            });
        }

        public async ValueTask RunTask(object?[] arguments)
            => await _binder._interpreter.RunAsync(CreateCall(arguments)).ConfigureAwait(false);

        public async ValueTask<T> RunTaskOf<T>(object?[] arguments)
        {
            var result = await _binder._interpreter.RunAsync(CreateCall(arguments)).ConfigureAwait(false);
            return result == null ? default! : (T)ClrCodec.Decode(typeof(T), result)!;
        }

        private ScriptCall CreateCall(object?[] arguments)
        {
            var json = new JsonArray();
            var token = CancellationToken.None;
            object? pipe = null;
            for (var i = 0; i < arguments.Length; i++)
            {
                var type = _parameterTypes[i];
                if (i == 0 && type.IsAssignableFrom(_nexusType) && type != typeof(object))
                    continue;

                if (type == typeof(CancellationToken))
                    token = (CancellationToken)arguments[i]!;
                else if (type == typeof(INexusDuplexPipe)
                         || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(INexusDuplexChannel<>)))
                    pipe = arguments[i];
                else if (_slot.Method == null)
                    json.Add(JsonValue.Create(arguments[i] is bool b ? b : false)); // OnConnected(isReconnected)
                else
                    json.Add(ClrCodec.Encode(type, arguments[i]));
            }

            if (!token.CanBeCanceled)
                token = _binder._lifetime();
            return new ScriptCall(_slot, json, token, pipe);
        }
    }
}
