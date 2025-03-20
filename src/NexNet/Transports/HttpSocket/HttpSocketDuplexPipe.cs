﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
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
    private readonly TaskCompletionSource? _pipeClosedTcs;
    //private readonly CancellationTokenRegistration? _serverConnectionAbortedRegistration;
    //private readonly CancellationTokenRegistration? _lifetimeApplicationStoppingRegistration;
    
    /// <summary>
    /// Task completed upon the completion of the pipe.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public Task PipeClosedCompletion => _pipeClosedTcs?.Task ?? throw new InvalidOperationException("This can only be used on servers.");

    /// <inheritdoc />
    public PipeReader Input => _inputPipe;

    /// <inheritdoc />
    public PipeWriter Output => _outputPipe;

    
    public HttpSocketDuplexPipe(Stream stream)
    {
        Debug.Assert(stream.CanWrite);
        Debug.Assert(stream.CanRead);
        _stream = stream;
        _inputPipe = PipeReader.Create(stream ?? throw new ArgumentNullException(nameof(stream)), new StreamPipeReaderOptions(leaveOpen: true));
        _outputPipe = PipeWriter.Create(stream);
    }
    
    public HttpSocketDuplexPipe(Stream stream, 
        CancellationToken serverConnectionAborted,
        CancellationToken lifetimeApplicationStopping)
    {
        Debug.Assert(stream.CanWrite);
        Debug.Assert(stream.CanRead);
        _stream = stream;
        _inputPipe = new NotifiedCompletePipeReader(this, stream ?? throw new ArgumentNullException(nameof(stream)));
        //_inputPipe = PipeReader.Create(stream ?? throw new ArgumentNullException(nameof(stream)), new StreamPipeReaderOptions(leaveOpen: true));
        _outputPipe = new NotifiedCompletePipeWriter(this, stream);
        //_outputPipe = PipeWriter.Create(stream);
        _pipeClosedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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

    private class NotifiedCompletePipeWriter : PipeWriter
    {
        private readonly HttpSocketDuplexPipe _duplexPipe;
        private readonly PipeWriter _pipeWriter;

        public NotifiedCompletePipeWriter(HttpSocketDuplexPipe duplexPipe, Stream stream)
        {
            _duplexPipe = duplexPipe;
            _pipeWriter = Create(stream);
        }
        public override void Advance(int bytes)
        {
            _pipeWriter.Advance(bytes);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            return _pipeWriter.GetMemory(sizeHint);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            return _pipeWriter.GetSpan(sizeHint);
        }

        public override void CancelPendingFlush()
        {
            _pipeWriter.CancelPendingFlush();
        }

        public override void Complete(Exception? exception = null)
        {
            _duplexPipe._pipeClosedTcs!.TrySetResult();
            _pipeWriter.Complete(exception);
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            return _pipeWriter.FlushAsync(cancellationToken);
        }
    }
    
    private class NotifiedCompletePipeReader : PipeReader {
        private readonly HttpSocketDuplexPipe _duplexPipe;
        private readonly PipeReader _pipeReader;

        public NotifiedCompletePipeReader(HttpSocketDuplexPipe duplexPipe, Stream stream)
        {
            _duplexPipe = duplexPipe;
            _pipeReader = Create(stream);
        }
        public override void AdvanceTo(SequencePosition consumed)
        {
            _pipeReader.AdvanceTo(consumed);
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            _pipeReader.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead()
        {
            _pipeReader.CancelPendingRead();
        }

        public override void Complete(Exception? exception = null)
        {
            _duplexPipe._pipeClosedTcs!.TrySetResult();
            _pipeReader.Complete();
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            return _pipeReader.ReadAsync(cancellationToken);
        }

        public override bool TryRead(out ReadResult result)
        {
            return _pipeReader.TryRead(out result);
        }
    }
}
