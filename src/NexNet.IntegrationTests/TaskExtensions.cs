using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal static class TaskExtensions
{
    public static Task Timeout(this Task task, double seconds)
    {
        return task.WaitAsync(TimeSpan.FromSeconds(seconds));
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
