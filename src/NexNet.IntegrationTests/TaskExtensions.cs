﻿using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal static class TaskExtensions
{
    public static Task Timeout(this Task task, double seconds)
    {
        return task.WaitAsync(TimeSpan.FromSeconds(seconds));
    }

    public static Task Timeout(this ValueTask task, double seconds)
    {
        return task.AsTask().WaitAsync(TimeSpan.FromSeconds(seconds));
    }

    public static Task<T> Timeout<T>(this Task<T> task, double seconds)
    {
        return task.WaitAsync(TimeSpan.FromSeconds(seconds));
    }

    public static Task<T> Timeout<T>(this ValueTask<T> task, double seconds)
    {
        return task.AsTask().WaitAsync(TimeSpan.FromSeconds(seconds));
    }

    public static async Task AssertTimeout(this Task task, double seconds)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(seconds));
            Assert.Fail("Task did not timeout.");
        }
        catch (TimeoutException)
        {
            return;
        }
        catch (Exception e)
        {
            Assert.Fail($"Task threw exception when one was not expected. {e}");
        }
    }


}
