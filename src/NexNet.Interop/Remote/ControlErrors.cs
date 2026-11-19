using System.Text.Json.Nodes;

namespace NexNet.Interop.Remote;

/// <summary>
/// The error objects of the control protocol (<c>{kind, message, detail}</c>, §8): client error kinds
/// (<see cref="ErrorKind"/>) and the harness kinds <c>Timeout</c>, <c>NotSupported</c>, <c>BadRequest</c>,
/// <c>UnknownHandle</c> and <c>Internal</c>.
/// </summary>
public static class ControlErrors
{
    /// <summary>The op's <c>timeoutMs</c> passed.</summary>
    public const string Timeout = "Timeout";

    /// <summary>The host or its client cannot do this.</summary>
    public const string NotSupported = "NotSupported";

    /// <summary>Malformed arguments, an unknown op or a scope that is not open.</summary>
    public const string BadRequest = "BadRequest";

    /// <summary>An unknown, released or foreign handle.</summary>
    public const string UnknownHandle = "UnknownHandle";

    /// <summary>A bug in the host.</summary>
    public const string Internal = "Internal";

    /// <summary>The C# exception of an error object.</summary>
    public static Exception ToException(JsonNode? error)
    {
        var obj = error as JsonObject;
        var kind = obj?["kind"]?.GetValue<string>() ?? Internal;
        var message = obj?["message"]?.GetValue<string>() ?? "";
        var detail = obj?["detail"] as JsonObject;

        if (Enum.TryParse<ErrorKind>(kind, ignoreCase: false, out var errorKind) && Enum.IsDefined(errorKind))
        {
            var info = new RemoteErrorInfo(errorKind, message)
            {
                Type = detail?["type"]?.GetValue<string>(),
                Code = (int?)Number(detail?["code"]),
                Reason = detail?["reason"]?.GetValue<string>(),
                Size = Number(detail?["size"]),
                Limit = Number(detail?["limit"]),
                Param = detail?["param"]?.GetValue<string>(),
            };
            return info.ToException();
        }

        return kind switch
        {
            Timeout => new TimeoutException(message),
            NotSupported => new NotSupportedException(message),
            BadRequest => new ArgumentException(message),
            UnknownHandle => new InvalidOperationException(message),
            _ => new RemoteHostException(kind, message, detail?["stack"]?.GetValue<string>()),
        };
    }

    /// <summary>The error object of a C# exception (used by C# hosts and for forwarded script calls).</summary>
    public static JsonObject FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
            exception = aggregate.InnerExceptions[0];

        string kind;
        var detail = new JsonObject();
        switch (exception)
        {
            case TimeoutException:
                kind = Timeout;
                break;
            case NotSupportedException:
                kind = NotSupported;
                break;
            // A client argument error (a negative list index), not a malformed request.
            case ArgumentOutOfRangeException:
                return ClientError(exception);
            case ArgumentException:
                kind = BadRequest;
                break;
            case RemoteHostException remote:
                kind = remote.Kind;
                break;
            default:
                return ClientError(exception);
        }

        return Create(kind, exception.Message, detail);
    }

    private static JsonObject ClientError(Exception exception)
    {
        var info = ErrorKinds.FromException(exception);
        var detail = new JsonObject();
        if (info.Type != null)
            detail["type"] = info.Type;
        if (info.Code != null)
            detail["code"] = info.Code;
        if (info.Reason != null)
            detail["reason"] = info.Reason;
        if (info.Size != null)
            detail["size"] = info.Size;
        if (info.Limit != null)
            detail["limit"] = info.Limit;
        if (info.Param != null)
            detail["param"] = info.Param;
        return Create(info.Kind.ToString(), info.Message, detail);
    }

    /// <summary>An integer member, whether parsed from the wire or built in memory.</summary>
    internal static long? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<long>(out var l))
            return l;
        if (value.TryGetValue<int>(out var i))
            return i;
        return long.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Creates an error object.</summary>
    public static JsonObject Create(string kind, string message, JsonObject? detail = null)
    {
        var error = new JsonObject { ["kind"] = kind, ["message"] = message };
        if (detail is { Count: > 0 })
            error["detail"] = detail;
        return error;
    }
}
