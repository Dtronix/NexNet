using System.Buffers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace NexNet.IntegrationTests;

internal class Utilities
{
    public static ReadOnlySequence<byte> GetBytes<T>(T data)
        where T : unmanaged
    {
        byte[] dataBytes;
        if (typeof(T) == typeof(int))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, int>(ref data));
        }
        else if (typeof(T) == typeof(uint))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, uint>(ref data));
        }
        else if (typeof(T) == typeof(long))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, long>(ref data));
        }
        else if (typeof(T) == typeof(ulong))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, ulong>(ref data));
        }
        else if (typeof(T) == typeof(short))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, short>(ref data));
        }
        else if (typeof(T) == typeof(ushort))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, ushort>(ref data));
        }
        else if (typeof(T) == typeof(byte))
        {
            dataBytes = new byte[] { Unsafe.As<T, byte>(ref data) };
        }
        else if (typeof(T) == typeof(sbyte))
        {
            dataBytes = new[] { (byte)Unsafe.As<T, sbyte>(ref data) };
        }
        else if (typeof(T) == typeof(float))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, float>(ref data));
        }
        else if (typeof(T) == typeof(double))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, double>(ref data));
        }
        else if (typeof(T) == typeof(char))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, char>(ref data));
        }
        else if (typeof(T) == typeof(bool))
        {
            dataBytes = BitConverter.GetBytes(Unsafe.As<T, bool>(ref data));
        }    
        else if (typeof(T) == typeof(decimal))
        {
            Span<int> int32s = stackalloc int[4];
            decimal.GetBits(Unsafe.As<T, decimal>(ref data), int32s);
            dataBytes = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                BitConverter.TryWriteBytes(new Span<byte>(dataBytes).Slice(i * 4), int32s[i]);
            }
        }
        else
        {
            throw new NotSupportedException();
        }

        return new ReadOnlySequence<byte>(dataBytes);
    }

    public static T GetValue<T>(byte[] data)
        where T : unmanaged
    {
        if (typeof(T) == typeof(int))
        {
            var converted = BitConverter.ToInt32(data, 0);
            return Unsafe.As<int, T>(ref converted);
        }
        else if (typeof(T) == typeof(uint))
        {
            var converted = BitConverter.ToUInt32(data, 0);
            return Unsafe.As<uint, T>(ref converted);
        }
        else if (typeof(T) == typeof(long))
        {
            var converted = BitConverter.ToInt64(data, 0);
            return Unsafe.As<long, T>(ref converted);
        }
        else if (typeof(T) == typeof(ulong))
        {
            var converted = BitConverter.ToUInt64(data, 0);
            return Unsafe.As<ulong, T>(ref converted);
        }
        else if (typeof(T) == typeof(short))
        {
            var converted = BitConverter.ToInt16(data, 0);
            return Unsafe.As<short, T>(ref converted);
        }
        else if (typeof(T) == typeof(ushort))
        {
            var converted = BitConverter.ToUInt16(data, 0);
            return Unsafe.As<ushort, T>(ref converted);
        }
        else if (typeof(T) == typeof(byte))
        {
            return Unsafe.As<byte, T>(ref data[0]);
        }
        else if (typeof(T) == typeof(sbyte))
        {
            return Unsafe.As<sbyte, T>(ref Unsafe.As<byte, sbyte>(ref data[0]));
        }
        else if (typeof(T) == typeof(float))
        {
            var converted = BitConverter.ToSingle(data, 0);
            return Unsafe.As<float, T>(ref converted);
        }
        else if (typeof(T) == typeof(double))
        {
            var converted = BitConverter.ToDouble(data, 0);
            return Unsafe.As<double, T>(ref converted);
        }
        else if (typeof(T) == typeof(char))
        {
            var converted = BitConverter.ToChar(data, 0);
            return Unsafe.As<char, T>(ref converted);
        }
        else if (typeof(T) == typeof(bool))
        {
            var converted = BitConverter.ToBoolean(data, 0);
            return Unsafe.As<bool, T>(ref converted);
        }
        else if (typeof(T) == typeof(decimal))
        {
            Span<int> int32s = stackalloc int[4];
            for (int i = 0; i < 4; i++)
            {
                int32s[i] = BitConverter.ToInt32(data.AsSpan(i * 4, 4));
            }

            var converted = new decimal(int32s);
            return Unsafe.As<decimal, T>(ref converted);
        }
        else
        {
            throw new NotSupportedException();
        }
    }
    
    /// <summary>
    /// Starts the async operation, then yields back to the TaskScheduler,
    /// then fires <paramref name="onAwait"/>, then awaits the rest.
    /// </summary>
    public static async Task InvokeAndNotifyAwait(
        Func<Task> task,
        Action onAwait)
    {
        // Kick off the async method (runs synchronously up to its first await)
        var runningTask = task();

        // force an asynchronous “hop” back to the scheduler
        await Task.Yield();

        // now we know the asyncMethod has already registered its continuation,
        // and we're running on a thread‐pool (or captured) context
        onAwait();

        // finally await the original task to observe its completion/exceptions
        await runningTask;
    }

    /// <summary>
    /// Starts the async operation, then yields back to the TaskScheduler,
    /// then fires <paramref name="onAwait"/>, then awaits the rest.
    /// </summary>
    public static async Task<T> InvokeAndNotifyAwait<T>(
        Func<Task<T>> asyncMethod,
        Action onAwait)
    {
        Task<T> task = asyncMethod();
        await Task.Yield();
        onAwait();
        return await task;
    }
    
    public static async Task WaitForConnectionClosureAsync(TcpClient tcpClient)
    {
        if (tcpClient == null)
            throw new ArgumentNullException(nameof(tcpClient));

        if (!tcpClient.Connected)
            return; // Already disconnected

        var stream = tcpClient.GetStream();
        var buffer = new byte[1];

        try
        {
            while (tcpClient.Connected)
            {
                // Try to read from the stream - this will return 0 when the connection is closed
                var bytesRead = await stream.ReadAsync(buffer, 0, 1);
                
                if (bytesRead == 0)
                {
                    // Connection closed by server
                    break;
                }
                
                // If we actually read data, we need to handle it appropriately
                // This implementation assumes you want to detect closure, not consume data
                // In a real scenario, you might want to buffer this data or handle it differently
            }
        }
        catch (ObjectDisposedException)
        {
            // Connection was disposed
        }
        catch (InvalidOperationException)
        {
            // Connection was closed
        }
        catch (OperationCanceledException)
        {
            // Operation was cancelled
            throw;
        }
    }
}
