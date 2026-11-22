#pragma warning disable CS8618
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.TestInterfaces;

/// <summary>
/// A server nexus with plain methods and methods that run in the separate invocation pool.
/// </summary>
public partial interface ISeparatePoolServerNexus
{
    ValueTask Plain(int id);

    [NexusMethod(SeparatePool = true)]
    ValueTask Slow(int id);

    [NexusMethod(SeparatePool = true)]
    ValueTask SlowWithCancellation(int id, CancellationToken cancellationToken);

    [NexusMethod(SeparatePool = true)]
    ValueTask<bool> SlowIsOnThreadPool();
}

public partial interface ISeparatePoolClientNexus { }

[Nexus<ISeparatePoolServerNexus, ISeparatePoolClientNexus>(NexusType = NexusType.Server)]
public partial class SeparatePoolServerNexus
{
    public Func<int, ValueTask>? PlainHandler;
    public Func<int, ValueTask>? SlowHandler;
    public Func<int, CancellationToken, ValueTask>? SlowWithCancellationHandler;

    public ValueTask Plain(int id) => PlainHandler?.Invoke(id) ?? ValueTask.CompletedTask;

    public ValueTask Slow(int id) => SlowHandler?.Invoke(id) ?? ValueTask.CompletedTask;

    public ValueTask SlowWithCancellation(int id, CancellationToken cancellationToken)
        => SlowWithCancellationHandler?.Invoke(id, cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask<bool> SlowIsOnThreadPool() => ValueTask.FromResult(Thread.CurrentThread.IsThreadPoolThread);
}

[Nexus<ISeparatePoolClientNexus, ISeparatePoolServerNexus>(NexusType = NexusType.Client)]
public partial class SeparatePoolClientNexus { }
