using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Logging;

namespace NexNet.Transports.WebSocket;

/// <summary>
/// Basic implementation that simply wraps <see cref="WebSocket"/> and exposes 
/// input and output pipes.
/// </summary>
internal class WebSocketPipe : IWebSocketPipe
{
    // Wait 250 ms before giving up on a Close, same as SignalR WebSocketHandler
    static readonly TimeSpan closeTimeout = TimeSpan.FromMilliseconds(250);
    
    private const int DefaultBufferSize = 8192;

    readonly CancellationTokenSource _disposeCancellation = new CancellationTokenSource();
    readonly Pipe _inputPipe;
    readonly PipeWriter _outputWriter;

    readonly System.Net.WebSockets.WebSocket _webSocket;
    readonly WebSocketPipeOptions _options;

    bool _completed;
    private readonly ConfigBase _config;

    public WebSocketPipe(
        System.Net.WebSockets.WebSocket webSocket, 
        WebSocketPipeOptions options,
        ConfigBase config)
    {
        _config = config;
        this._webSocket = webSocket;
        this._options = options;
        _inputPipe = new Pipe(options.InputPipeOptions);
        _outputWriter = PipeWriter.Create(new WebSocketStream(webSocket, config));
    }

    bool IsClient => _webSocket is ClientWebSocket;

    public PipeReader Input => _inputPipe.Reader;

    public PipeWriter Output => _outputWriter;

    public WebSocketCloseStatus? CloseStatus => _webSocket.CloseStatus;

    public string? CloseStatusDescription => _webSocket.CloseStatusDescription;

    public WebSocketState State => _webSocket.State;

    public string? SubProtocol => _webSocket.SubProtocol;

    public Task RunAsync(CancellationToken cancellation = default)
    {
        if (_webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException($"WebSocket must be opened. State was {_webSocket.State}");

        var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _disposeCancellation.Token);
        return ReadInputAsync(combined.Token);
    }

    public async ValueTask CompleteAsync(WebSocketCloseStatus? closeStatus = null,
        string? closeStatusDescription = null)
    {
        if (_completed)
            return;

        _completed = true;

        // NOTE: invoking these more than once is no-op.
        await _inputPipe.Writer.CompleteAsync().ConfigureAwait(false);
        await _inputPipe.Reader.CompleteAsync().ConfigureAwait(false);

        if (_options.CloseWhenCompleted || closeStatus != null)
            await CloseAsync(closeStatus ?? WebSocketCloseStatus.NormalClosure, closeStatusDescription ?? "").ConfigureAwait(false);
    }

    async ValueTask CloseAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription)
    {
        var state = State;
        if (state is WebSocketState.Closed or WebSocketState.CloseSent or WebSocketState.Aborted)
            return;

        var closeTask = IsClient ?
            // Disconnect from client vs server is different.
            _webSocket.CloseAsync(closeStatus, closeStatusDescription, default) :
            _webSocket.CloseOutputAsync(closeStatus, closeStatusDescription, default);

        // Don't wait indefinitely for the close to be acknowledged
        await Task.WhenAny(closeTask, Task.Delay(closeTimeout)).ConfigureAwait(false);
    }

    async Task ReadInputAsync(CancellationToken cancellation)
    {
        bool enableTransportLog = (_config.Logger?.Behaviors & NexusLogBehaviors.LogTransportData) != 0;
        while (_webSocket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
        {
            try
            {
                var buffer = _inputPipe.Writer.GetMemory(DefaultBufferSize);
                var message = await _webSocket.ReceiveAsync(buffer, cancellation).ConfigureAwait(false);
                
                while (!cancellation.IsCancellationRequested && !message.EndOfMessage && message.MessageType != WebSocketMessageType.Close)
                {
                    if (message.Count == 0)
                        break;
                    
                    if (enableTransportLog)
                        _config.Logger!.LogTraceArray($"Received {message.Count}:", buffer.Slice(0, message.Count));

                    _inputPipe.Writer.Advance(message.Count);
                    buffer = _inputPipe.Writer.GetMemory(DefaultBufferSize);
                   
                    message = await _webSocket.ReceiveAsync(buffer, cancellation).ConfigureAwait(false);
                }

                // We didn't get a complete message, we can't flush partial message.
                if (cancellation.IsCancellationRequested || !message.EndOfMessage || message.MessageType == WebSocketMessageType.Close)
                    break;
                
                if (enableTransportLog)
                    _config.Logger!.LogTraceArray($"Received {message.Count}:", buffer.Slice(0, message.Count));

                // Advance the EndOfMessage bytes before flushing.
                _inputPipe.Writer.Advance(message.Count);
                var result = await _inputPipe.Writer.FlushAsync(cancellation).ConfigureAwait(false);
                if (result.IsCompleted)
                    break;

            }
            catch (Exception ex) when (ex is OperationCanceledException ||
                                       ex is WebSocketException ||
                                       ex is InvalidOperationException)
            {
                break;
            }
        }

        // Preserve the close status since it might be triggered by a received Close message containing the status and description.
        await CompleteAsync(_webSocket.CloseStatus, _webSocket.CloseStatusDescription).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposeCancellation.Cancel();
        _webSocket.Dispose();
    }

    class WebSocketStream : Stream
    {
        private readonly System.Net.WebSockets.WebSocket _webSocket;
        private readonly ConfigBase _config;

        public WebSocketStream(System.Net.WebSockets.WebSocket webSocket, ConfigBase config)
        {
            this._webSocket = webSocket;
            _config = config;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if((_config.Logger?.Behaviors & NexusLogBehaviors.LogTransportData) != 0)
                _config.Logger!.LogTraceArray($"Sent {buffer.Length}:", buffer);
            
            return _webSocket.SendAsync(buffer, WebSocketMessageType.Binary, true, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => throw new NotImplementedException();
        public override bool CanSeek => throw new NotImplementedException();
        public override bool CanWrite => throw new NotImplementedException();
        public override long Length => throw new NotImplementedException();
        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public override void Flush() => throw new NotImplementedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotImplementedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
        public override void SetLength(long value) => throw new NotImplementedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
    }
}
