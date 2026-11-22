using System.Text.Json.Nodes;

namespace NexNet.Interop.Remote;

/// <summary>Runs the calls of generated remote proxies as <c>invoke</c> requests (control protocol §6.5).</summary>
internal sealed class RemoteInvoker(RemoteClientDriver driver) : IRemoteInvoker
{
    public void Invoke(RemoteInvocation invocation)
    {
        // Fire and forget: the host answers once the invocation is handed to its client runtime.
        var args = BuildArguments(invocation, out var token);
        args["detach"] = true;
        var task = driver.Post("invoke", args, timeoutMs: 0);
        if (token != null)
            task.ContinueWith(_ => token.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask InvokeAsync(RemoteInvocation invocation) => await CallAsync(invocation).ConfigureAwait(false);

    public async ValueTask<JsonNode?> InvokeWithResultAsync(RemoteInvocation invocation)
        => (await CallAsync(invocation).ConfigureAwait(false))["result"]?.DeepClone();

    public IRemoteList<T> GetList<T>(RemoteCollection collection, TypedCodec<T> codec) => new RemoteList<T>(driver, collection, codec);

    private async Task<JsonObject> CallAsync(RemoteInvocation invocation)
    {
        // Queued before the first await, so invocations are sent in call order.
        var args = BuildArguments(invocation, out var token);
        var call = driver.CallAsync("invoke", args, timeoutMs: 0);
        try
        {
            return await call.ConfigureAwait(false);
        }
        finally
        {
            token?.Dispose();
        }
    }

    private JsonObject BuildArguments(RemoteInvocation invocation, out RemoteToken? token)
    {
        token = null;
        var args = new JsonObject
        {
            ["h"] = driver.Handle,
            ["method"] = ControlJson.ToJson(invocation.Method),
            ["args"] = invocation.Arguments.DeepClone(),
        };

        if (invocation.Pipe != null)
        {
            args["pipe"] = (invocation.Pipe as RemotePipe ?? throw new ArgumentException(
                $"{invocation.Method} needs a pipe created by this client's CreatePipeAsync.")).Handle;
        }

        args["cancel"] = Cancel(driver, invocation.Cancel, out token);
        if (args["cancel"] == null)
            args.Remove("cancel");
        return args;
    }

    /// <summary>The <c>cancel</c> member of a request, creating a runner-named token for a token cancellation.</summary>
    internal static JsonObject? Cancel(RemoteClientDriver driver, RemoteCancel cancel, out RemoteToken? token)
    {
        token = null;
        switch (cancel.Kind)
        {
            case RemoteCancelKind.After:
                return new JsonObject { ["afterMs"] = cancel.AfterMilliseconds };
            case RemoteCancelKind.Token:
                token = driver.CreateToken(cancel.Token);
                return new JsonObject { ["token"] = token.Name };
            default:
                return null;
        }
    }
}
