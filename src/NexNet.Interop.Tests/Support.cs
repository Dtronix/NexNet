using NexNet.Interop.Remote;
using NexNet.Interop.Tests.Fake;

namespace NexNet.Interop.Tests;

/// <summary>A control connection to a fake host over a real loopback socket.</summary>
internal sealed class FakeConnection : IAsyncDisposable
{
    private FakeConnection(ControlListener listener, ControlConnection connection, FakeHost host)
    {
        Listener = listener;
        Connection = connection;
        Host = host;
    }

    public ControlListener Listener { get; }

    public ControlConnection Connection { get; }

    public FakeHost Host { get; }

    public static async Task<FakeConnection> OpenAsync(FakeHostOptions? options = null)
    {
        var listener = ControlListener.Start();
        var token = ControlListener.CreateToken();
        var accept = listener.AcceptAsync(token, null, null);
        var host = await FakeHost.ConnectAsync(listener.ControlArgument, token, options ?? new FakeHostOptions());
        var connection = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        return new FakeConnection(listener, connection, host);
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await Host.DisposeAsync();
        Listener.Dispose();
    }
}

/// <summary>A stream that returns at most one byte per read, to test framing across partial reads.</summary>
internal sealed class TrickleStream(byte[] data) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= data.Length || count == 0)
            return 0;
        buffer[offset] = data[_position++];
        return 1;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
