using NexNet.Interop.Remote;

namespace NexNet.Interop.Hosts;

/// <summary>
/// Retries a test once, only when it failed because its language host crashed (<see cref="HostCrashedException"/>
/// anywhere in the exception chain). Every other failure stands.
/// </summary>
public sealed class RetryOnHostCrashAttribute : RetryAttribute
{
    /// <summary>Retries at most once.</summary>
    public RetryOnHostCrashAttribute()
        : base(1)
    {
    }

    /// <inheritdoc />
    public override Task<bool> ShouldRetry(TestContext context, Exception exception, int currentRetryCount)
        => Task.FromResult(currentRetryCount <= 1 && IsHostCrash(exception));

    /// <summary>True when <paramref name="exception"/> or an exception it wraps is a <see cref="HostCrashedException"/>.</summary>
    public static bool IsHostCrash(Exception? exception)
    {
        for (var depth = 0; exception != null && depth < 32; depth++)
        {
            if (exception is HostCrashedException)
                return true;
            if (exception is AggregateException aggregate)
                return aggregate.InnerExceptions.Any(IsHostCrash);
            exception = exception.InnerException;
        }

        return false;
    }
}
