using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NexNet.Internals;

internal record class RunArguments<T1>(Func<T1, ValueTask> InvokeAction, T1 Param1);
internal record class RunArguments<T1, T2>(Func<T1, T2, ValueTask> InvokeAction, T1 Param1, T2 Param2);

internal static class TaskUtilities
{
    public static void StartTask<T1>(
        RunArguments<T1> args, 
        TaskCreationOptions options = TaskCreationOptions.LongRunning)
    {
        if (args == null)
            throw new ArgumentNullException(nameof(args));

        _ = Task.Factory.StartNew(static (object? argObject) =>
        {
            var args = Unsafe.As<RunArguments<T1>>(argObject)!;
            return args.InvokeAction.Invoke(args.Param1);

        }, args, options);
    }

    public static void StartTask<T1, T2>(
        RunArguments<T1, T2> args,
        TaskCreationOptions options = TaskCreationOptions.LongRunning)
    {
        if (args == null)
            throw new ArgumentNullException(nameof(args));

        _ = Task.Factory.StartNew(static (object? argObject) =>
        {
            var args = Unsafe.As<RunArguments<T1, T2>>(argObject)!;
            return args.InvokeAction.Invoke(args.Param1, args.Param2);

        }, args, options);
    }
}
