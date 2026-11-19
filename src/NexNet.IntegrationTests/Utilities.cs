using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Transports;

namespace NexNet.IntegrationTests;

internal static class Utilities
{
    public static T Dequeue<T>(this ConcurrentQueue<T> queue)
    {
        if (queue.TryDequeue(out var item))
            return item;

        throw new Exception("Queue is empty.");
    }
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

/// <summary>
/// Buffers test log output and only writes to the test output if the test fails.
/// Use in tests to reduce noise from passing tests while preserving diagnostic info for failures.
/// </summary>
internal sealed class BufferedTestLogger
{
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();

    /// <summary>
    /// Logs a message to the buffer.
    /// </summary>
    public void Log(string message)
    {
        lock (_lock)
        {
            _buffer.AppendLine(message);
        }
    }

    /// <summary>
    /// Clears the buffer. Call in SetUp to reset state between tests.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }

    /// <summary>
    /// Writes buffered logs to the test output only if the current test failed.
    /// Call in TearDown.
    /// </summary>
    public void FlushOnFailure()
    {
        if (TestContext.Current?.Execution.Result?.State is TestState.Failed or TestState.Timeout)
        {
            Flush();
        }
    }

    /// <summary>
    /// Writes all buffered logs to the test output unconditionally.
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_buffer.Length > 0)
            {
                TestContext.Current?.Output.StandardOutput.Write(_buffer.ToString());
            }
        }
    }
}
/// <summary>
/// A NexNet frame seen by a <see cref="ServerTaps"/> tap.
/// </summary>
/// <param name="Type">The frame's message type.</param>
/// <param name="Body">The frame's body, without the header and post-header; empty for header-only frames (ping and
/// disconnect).</param>
/// <param name="Frame">The whole frame as it is on the wire: header, post-header and body.</param>
internal readonly record struct TappedFrame(MessageType Type, byte[] Body, byte[] Frame);

/// <summary>
/// Server-side observation of the frames a server exchanges with its clients. Tests observe a client's wire behavior
/// here, on the C# server, rather than with taps inside the client, so the same test works for a client written in
/// any language.
/// </summary>
internal static class ServerTaps
{
    /// <summary>
    /// Calls <paramref name="onFrame"/> with every frame the server receives, in order, before the session handles it.
    /// Replaces the server configuration's <c>InternalOnReceive</c>.
    /// </summary>
    public static void OnReceive(ServerConfig config, Action<INexusSession, TappedFrame> onFrame)
        => OnReceive(config, (session, frame) =>
        {
            onFrame(session, frame);
            return ValueTask.CompletedTask;
        });

    /// <inheritdoc cref="OnReceive(ServerConfig, Action{INexusSession, TappedFrame})"/>
    /// <remarks>The session's read loop waits for <paramref name="onFrame"/>, so a tap can hold the session's
    /// receiving.</remarks>
    public static void OnReceive(ServerConfig config, Func<INexusSession, TappedFrame, ValueTask> onFrame)
    {
        var readers = new ConditionalWeakTable<INexusSession, FrameReader>();
        config.InternalOnReceive = async (session, buffer) =>
        {
            var reader = readers.GetValue(session, static _ => new FrameReader());

            // The tap sees the session's whole unconsumed input on each read. The bytes the session has consumed so
            // far give the stream offset of this buffer, so only the new bytes are added.
            var start = session.GetSnapshot().BytesReceived;
            foreach (var frame in reader.Add(buffer, start))
                await onFrame(session, frame).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Calls <paramref name="onSession"/> with every session the server creates, once the transport has connected and
    /// before any byte is read. Replaces the server configuration's <c>InternalOnSessionSetup</c>.
    /// </summary>
    public static void OnSessionCreated(ServerConfig config, Action<INexusSession> onSession)
        => config.InternalOnSessionSetup = onSession;

    /// <summary>
    /// Calls <paramref name="onFrame"/> with every frame the server sends. Replaces the server configuration's
    /// <c>InternalOnSend</c>.
    /// </summary>
    public static void OnSend(ServerConfig config, Action<INexusSession, TappedFrame> onFrame)
    {
        config.InternalOnSend = (session, bytes) =>
        {
            if (bytes.Length == 0)
                return;

            onFrame(session, new TappedFrame((MessageType)bytes[0], bytes.Length > 3 ? bytes[3..] : [], bytes));
        };
    }

    /// <summary>
    /// Splits one session's received byte stream into frames (docs/internals/protocol-specification.md).
    /// </summary>
    private sealed class FrameReader
    {
        private const int PreambleLength = 8;
        private readonly List<byte> _pending = new();
        private long _seenEnd;
        private bool _preambleSkipped;
        private bool _started;

        public List<TappedFrame> Add(ReadOnlySequence<byte> buffer, long start)
        {
            // A tap installed on a running session starts where the session is; it reads at a frame boundary.
            if (!_started)
            {
                _started = true;
                _seenEnd = start;
                _preambleSkipped = start >= PreambleLength;
            }

            var end = start + buffer.Length;
            if (end > _seenEnd)
            {
                _pending.AddRange(buffer.Slice(_seenEnd - start).ToArray());
                _seenEnd = end;
            }

            var frames = new List<TappedFrame>();
            var position = 0;
            if (!_preambleSkipped)
            {
                if (_pending.Count < PreambleLength)
                    return frames;

                position = PreambleLength;
                _preambleSkipped = true;
            }

            while (position < _pending.Count)
            {
                var type = (MessageType)_pending[position];

                // Ping and the disconnect codes are a single byte.
                if (type == MessageType.Ping || _pending[position] is >= 20 and < 39)
                {
                    frames.Add(new TappedFrame(type, [], [_pending[position]]));
                    position++;
                    continue;
                }

                var headerLength = type switch
                {
                    MessageType.DuplexPipeWrite or MessageType.PipeWindowUpdate => 3 + sizeof(ushort),
                    MessageType.MessageChunk => 3 + sizeof(ushort) + 1,
                    _ => 3,
                };

                if (position + headerLength > _pending.Count)
                    break;

                var bodyLength = _pending[position + 1] | (_pending[position + 2] << 8);
                if (position + headerLength + bodyLength > _pending.Count)
                    break;

                frames.Add(new TappedFrame(
                    type,
                    _pending.GetRange(position + headerLength, bodyLength).ToArray(),
                    _pending.GetRange(position, headerLength + bodyLength).ToArray()));
                position += headerLength + bodyLength;
            }

            _pending.RemoveRange(0, position);
            return frames;
        }
    }
}
