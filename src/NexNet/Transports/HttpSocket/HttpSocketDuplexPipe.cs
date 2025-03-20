using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.HttpSocket;

/// <summary>
/// Basic implementation that simply wraps <see cref="WebSocket"/> and exposes 
/// input and output pipes.
/// </summary>
public class HttpSocketDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly Stream? _stream;
    readonly PipeReader _inputPipe;
    readonly PipeWriter _outputPipe;
    private readonly TaskCompletionSource? _serverClosedPipesTcs;
    
    public HttpSocketDuplexPipe(Stream stream, bool isServer)
    {
        Debug.Assert(stream.CanWrite);
        Debug.Assert(stream.CanRead);
        _stream = stream;
        _inputPipe = PipeReader.Create(stream ?? throw new ArgumentNullException(nameof(stream)), new StreamPipeReaderOptions(leaveOpen: true));
        _outputPipe = PipeWriter.Create(stream);

        if (isServer)
        {
            _serverClosedPipesTcs = new TaskCompletionSource();
        }
        
    }

    //public HttpSocketDuplexPipe(PipeReader requestBodyReader, PipeWriter responseBodyReader, TaskCompletionSource cts)
    //{
    //    _inputPipe = requestBodyReader;
    //    _outputPipe = responseBodyReader;
    //    _serverClosedPipesTcs = new TaskCompletionSource();
    //}
    
    public Task PipeClosedCompletion => _serverClosedPipesTcs?.Task ?? throw new InvalidOperationException("This can only be used on servers.");

    public PipeReader Input => _inputPipe;

    public PipeWriter Output => _outputPipe;

    public async ValueTask CompleteAsync()
    {
        try
        {
            await _inputPipe.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
        
        try
        {
            await _outputPipe.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        if (_stream != null)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        return CompleteAsync();
    }
}
