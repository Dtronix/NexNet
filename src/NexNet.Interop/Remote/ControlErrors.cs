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
                Code = detail?["code"]?.GetValue<int>(),
                Reason = detail?["reason"]?.GetValue<string>(),
                Size = detail?["size"]?.GetValue<long>(),
                Limit = detail?["limit"]?.GetValue<long>(),
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
            case ArgumentException:
                kind = BadRequest;
                break;
            case RemoteHostException remote:
                kind = remote.Kind;
                break;
            default:
                var info = ErrorKinds.FromException(exception);
                kind = info.Kind.ToString();
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
                return Create(kind, info.Message, detail);
        }

        return Create(kind, exception.Message, detail);
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
