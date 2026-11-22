using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using NexNet.Collections;
using NexNet.Interop.Reflection;
using NexNet.Invocation;

namespace NexNet.Interop.Remote;

/// <summary>The JSON forms the remote driver sends: client specs, method and collection references, type expressions.</summary>
public static class ControlJson
{
    /// <summary>The <c>ClientSpec</c> JSON of control protocol §6.4.1; null members are omitted.</summary>
    public static JsonObject ToJson(ClientSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var json = new JsonObject();
        if (spec.Transport is { } transport)
            json["transport"] = transport.ToString();
        json["address"] = spec.Address;
        Add(json, "tlsServerName", spec.TlsServerName);
        Add(json, "caPem", spec.CaPem);
        Add(json, "clientCertificatePem", spec.ClientCertificatePem);
        Add(json, "clientKeyPem", spec.ClientKeyPem);
        Add(json, "pinSha256", spec.PinSha256);
        if (spec.InsecureSkipVerify)
            json["insecureSkipVerify"] = true;
        Add(json, "connectionTimeoutMs", spec.ConnectionTimeoutMs);
        Add(json, "timeoutMs", spec.TimeoutMs);
        Add(json, "handshakeTimeoutMs", spec.HandshakeTimeoutMs);
        Add(json, "pingIntervalMs", spec.PingIntervalMs);
        if (spec.ReconnectDelaysMs != null)
            json["reconnectDelaysMs"] = new JsonArray(spec.ReconnectDelaysMs.Select(d => (JsonNode?)d).ToArray());
        if (spec.ReconnectContinuously)
            json["reconnectContinuously"] = true;
        if (spec.DefaultReconnection)
            json["defaultReconnection"] = true;
        if (spec.ReconnectReasons != null)
            json["reconnectReasons"] = new JsonArray(spec.ReconnectReasons.Select(r => (JsonNode?)r.ToString()).ToArray());
        Add(json, "authToken", spec.AuthToken);
        if (spec.Authenticate is { } authenticate)
        {
            var script = new JsonObject();
            Add(script, "token", authenticate.Token);
            if (authenticate.AppendCallNumber)
                script["appendCallNumber"] = true;
            if (authenticate.DelayMs != 0)
                script["delayMs"] = authenticate.DelayMs;
            if (authenticate.Hang)
                script["hang"] = true;
            Add(script, "throwMessage", authenticate.ThrowMessage);
            json["authenticate"] = script;
        }

        Add(json, "authorizationHeader", spec.AuthorizationHeader);
        if (spec.QueryToken is { } queryToken)
            json["queryToken"] = new JsonObject { ["name"] = queryToken.Name, ["value"] = queryToken.Value };
        Add(json, "maxMessageSize", spec.MaxMessageSize);
        Add(json, "invocationCredits", spec.InvocationCredits);
        Add(json, "maxConcurrentConnectionInvocations", spec.MaxConcurrentConnectionInvocations);
        Add(json, "maxSeparatePoolInvocations", spec.MaxSeparatePoolInvocations);
        Add(json, "invocationQueueWarningThresholdMs", spec.InvocationQueueWarningThresholdMs);
        Add(json, "sessionBudget", spec.SessionBudget);
        Add(json, "maxPipes", spec.MaxPipes);
        Add(json, "pipeWindow", spec.PipeWindow);
        Add(json, "progressTimeoutMs", spec.ProgressTimeoutMs);
        if (spec.RemoteExceptionDetail is { } detail)
            json["remoteExceptionDetail"] = detail.ToString();
        if (spec.OnInvocationException is { } error)
        {
            var hook = new JsonObject();
            Add(hook, "code", error.Code);
            Add(hook, "message", error.Message);
            json["onInvocationException"] = hook;
        }
        if (spec.FakeTime)
            json["fakeTime"] = true;
        if (spec.LogLevel is { } level)
            json["logLevel"] = level.ToString();
        return json;
    }

    /// <summary>The pool options JSON of control protocol §6.11.1; null members are omitted.</summary>
    public static JsonObject ToJson(ClientPoolSpec pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var json = new JsonObject();
        Add(json, "maxConnections", pool.MaxConnections);
        Add(json, "maxIdleTimeMs", pool.MaxIdleTimeMs);
        Add(json, "minIdleConnections", pool.MinIdleConnections);
        return json;
    }

    /// <summary>A method reference: <c>{nexus, name, id}</c>.</summary>
    public static JsonObject ToJson(RemoteMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return new JsonObject { ["nexus"] = method.InterfaceName, ["name"] = method.Name, ["id"] = method.Id };
    }

    /// <summary>A collection reference: <c>{nexus, name, id}</c>.</summary>
    public static JsonObject ToJson(RemoteCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return new JsonObject { ["nexus"] = collection.InterfaceName, ["name"] = collection.Name, ["id"] = collection.Id };
    }

    /// <summary>
    /// The data of a <c>list.changed</c> event (control protocol §7.6): unreported members are omitted, and the time
    /// travels as the event's <c>hostNs</c>.
    /// </summary>
    public static JsonObject ToJson<T>(string watch, RemoteListChange<T> change, TypedCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(codec);
        var json = new JsonObject
        {
            ["watch"] = watch,
            ["action"] = change.Action.ToString(),
        };
        if (change.Index is { } index)
            json["index"] = index;
        if (change.OldIndex is { } oldIndex)
            json["oldIndex"] = oldIndex;
        if (change.Items.Count > 0)
            json["items"] = new JsonArray(change.Items.Select(i => codec.Encode(i)).ToArray());
        if (change.OldItems.Count > 0)
            json["oldItems"] = new JsonArray(change.OldItems.Select(i => codec.Encode(i)).ToArray());
        return json;
    }

    /// <summary>The change of a <c>list.changed</c> event (control protocol §7.6); its time is the event's <c>hostNs</c>.</summary>
    /// <exception cref="ControlProtocolException">The action is missing or unknown.</exception>
    public static RemoteListChange<T> ToListChange<T>(ControlEvent evt, TypedCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var data = evt.Data;
        ArgumentNullException.ThrowIfNull(codec);
        var actionName = data["action"]?.GetValue<string>();
        if (!Enum.TryParse<NexusCollectionChangedAction>(actionName, ignoreCase: false, out var action)
            || !Enum.IsDefined(action) || action == NexusCollectionChangedAction.Unset)
            throw new ControlProtocolException($"list.changed has an unknown action '{actionName}'.");

        IReadOnlyList<T> Items(string name)
            => data[name] is JsonArray array ? array.Select(n => codec.Decode(n)!).ToArray() : Array.Empty<T>();
        return new RemoteListChange<T>(action, TimeSpan.FromTicks(evt.HostNs / 100))
        {
            Index = (int?)ControlErrors.Number(data["index"]),
            OldIndex = (int?)ControlErrors.Number(data["oldIndex"]),
            Items = Items("items"),
            OldItems = Items("oldItems"),
        };
    }

    /// <summary>A cancellation: <c>{afterMs}</c> or <c>{token}</c>; null for <see cref="RemoteCancelKind.None"/> and token kinds.</summary>
    public static JsonObject? AfterCancel(RemoteCancel cancel)
        => cancel.Kind == RemoteCancelKind.After ? new JsonObject { ["afterMs"] = cancel.AfterMilliseconds } : null;

    /// <summary>The JSON of a script step (control protocol §6.6).</summary>
    public static JsonObject ToJson(ScriptStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step switch
        {
            ReturnStep r => new JsonObject { ["step"] = "return", ["value"] = r.Value?.DeepClone() },
            ThrowStep t => new JsonObject { ["step"] = "throw", ["kind"] = t.Kind.ToString(), ["message"] = t.Message },
            DelayStep d => new JsonObject { ["step"] = "delay", ["ms"] = d.Milliseconds },
            AwaitSignalStep s => new JsonObject { ["step"] = "awaitSignal", ["name"] = s.Name },
            AwaitCancelStep => new JsonObject { ["step"] = "awaitCancel" },
            InvokeStep i => new JsonObject
            {
                ["step"] = "invoke",
                ["method"] = ToJson(i.Method),
                ["args"] = i.Arguments.DeepClone(),
                ["returnKind"] = i.Method.ReturnKind.ToString(),
            },
            NamedStep n => n.Options == null
                ? new JsonObject { ["step"] = "named", ["handler"] = n.Handler }
                : new JsonObject { ["step"] = "named", ["handler"] = n.Handler, ["options"] = n.Options.DeepClone() },
            ForwardStep => new JsonObject { ["step"] = "forward" },
            _ => throw new NotSupportedException("Unknown script step " + step.GetType().Name + "."),
        };
    }

    /// <summary>
    /// The golden type expression of a schema (tests/golden/README.md, "Types"), with declared types by full metadata
    /// name: the <c>type</c> of <c>channel.writer</c> / <c>channel.reader</c>.
    /// </summary>
    public static string TypeExpression(TypeSchema type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var sb = new StringBuilder();
        Print(sb, type);
        return sb.ToString();
    }

    /// <summary>
    /// The full metadata names of the client and server nexus interfaces of a C# nexus pair, as <c>client.create</c>
    /// sends them.
    /// </summary>
    public static (string Client, string Server) NexusPair<TClientNexus, TServerProxy>()
        => NexusPair(typeof(TClientNexus), typeof(TServerProxy));

    /// <summary>The full metadata names of the client and server nexus interfaces of a C# nexus pair.</summary>
    public static (string Client, string Server) NexusPair(Type clientNexus, Type serverProxy)
    {
        ArgumentNullException.ThrowIfNull(clientNexus);
        ArgumentNullException.ThrowIfNull(serverProxy);
        var client = UserInterfaces(clientNexus)
                         .FirstOrDefault(i => NexusTypes.FindScriptFacade(i.FullName!) != null)
                     ?? MostDerived(UserInterfaces(clientNexus))
                     ?? throw new ArgumentException($"{clientNexus} implements no client nexus interface.", nameof(clientNexus));
        var server = MostDerived(UserInterfaces(serverProxy))
                     ?? throw new ArgumentException($"{serverProxy} implements no server nexus interface.", nameof(serverProxy));
        return (client.FullName!, server.FullName!);
    }

    private static IEnumerable<Type> UserInterfaces(Type type)
    {
        var nexNet = typeof(ProxyInvocationBase).Assembly;
        return type.GetInterfaces().Where(i => i.Assembly != nexNet && i.FullName != null && !i.IsGenericType);
    }

    private static Type? MostDerived(IEnumerable<Type> interfaces)
    {
        var list = interfaces.ToList();
        return list.FirstOrDefault(i => !list.Any(other => other != i && i.IsAssignableFrom(other)));
    }

    private static void Add(JsonObject json, string name, string? value)
    {
        if (value != null)
            json[name] = value;
    }

    private static void Add(JsonObject json, string name, int? value)
    {
        if (value != null)
            json[name] = value.Value;
    }

    private static string PrimName(SchemaPrim prim) => prim switch
    {
        SchemaPrim.Bool => "bool",
        SchemaPrim.SByte => "sbyte",
        SchemaPrim.Byte => "byte",
        SchemaPrim.Int16 => "short",
        SchemaPrim.UInt16 => "ushort",
        SchemaPrim.Int32 => "int",
        SchemaPrim.UInt32 => "uint",
        SchemaPrim.Int64 => "long",
        SchemaPrim.UInt64 => "ulong",
        SchemaPrim.Single => "float",
        SchemaPrim.Double => "double",
        SchemaPrim.Half => "half",
        SchemaPrim.Char => "char",
        SchemaPrim.Decimal => "decimal",
        SchemaPrim.String => "string",
        SchemaPrim.None => throw new ArgumentOutOfRangeException(nameof(prim), prim, null),
        _ => prim.ToString(), // DateTime, DateTimeOffset, TimeSpan, DateOnly, TimeOnly, Guid, BigInteger, Uri, Version
    };

    private static void Print(StringBuilder sb, TypeSchema t)
    {
        switch (t.Kind)
        {
            case TypeSchemaKind.Prim:
                sb.Append(PrimName(t.Prim));
                break;
            case TypeSchemaKind.Bin:
                sb.Append("byte[]");
                break;
            case TypeSchemaKind.PrimArray:
                sb.Append(PrimName(t.Prim)).Append("[]");
                break;
            case TypeSchemaKind.Seq when t.Clr == "Array":
                Print(sb, t.Args[0]);
                sb.Append("[]");
                break;
            case TypeSchemaKind.Seq:
                Generic(sb, t.Clr ?? "IEnumerable", t.Args);
                break;
            case TypeSchemaKind.Set:
                Generic(sb, "HashSet", t.Args);
                break;
            case TypeSchemaKind.Queue:
                Generic(sb, "Queue", t.Args);
                break;
            case TypeSchemaKind.Stack:
                Generic(sb, "Stack", t.Args);
                break;
            case TypeSchemaKind.Map:
                Generic(sb, "Dictionary", t.Args);
                break;
            case TypeSchemaKind.Tuple:
                sb.Append('(');
                for (var i = 0; i < t.Args.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    Print(sb, t.Args[i]);
                }

                sb.Append(')');
                break;
            case TypeSchemaKind.Nullable:
                Print(sb, t.Args[0]);
                sb.Append('?');
                break;
            case TypeSchemaKind.Decl:
                if (t.Args.Count == 0)
                    sb.Append(t.DeclId);
                else
                    Generic(sb, t.DeclId!, t.Args);
                break;
            case TypeSchemaKind.TypeParam:
                sb.Append('$').Append(t.Ordinal.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(t.Clr ?? "raw");
                break;
        }
    }

    private static void Generic(StringBuilder sb, string name, IReadOnlyList<TypeSchema> args)
    {
        sb.Append(name).Append('<');
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            Print(sb, args[i]);
        }

        sb.Append('>');
    }
}
