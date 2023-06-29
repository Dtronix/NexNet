using System;
using System.Buffers;
using System.IO.Pipelines;
using NexNet.Cache;
using NexNet.Internals;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet;

public class NexNetPipe
{
    private readonly Mode _mode;
    private PipeWriter? _output;
    private readonly Pipe _pipe;
    private int _invocationId;

    /// <summary>
    /// Only valid on readers.
    /// </summary>
    internal BufferWriter<byte> BufferWriter { get; }

    private enum Mode
    {
        Reader,
        Writer
    }

    public PipeReader Input
    {
        get
        {
            if (_mode == Mode.Writer)
                throw new InvalidOperationException("Can't access the input from a writer.");
            return _pipe.Reader;
        }
    }

    public PipeWriter Output
    {
        get
        {
            if (_mode == Mode.Reader)
                throw new InvalidOperationException("Can't access the output from a reader.");
            return _output!;
        }
    }

    private NexNetPipe(Mode mode)
    {
        _mode = mode;

        if (mode == Mode.Writer)
            BufferWriter = BufferWriter<byte>.Create();

        _pipe = new Pipe();
    }

    public static NexNetPipe Create()
    {
        return new NexNetPipe(Mode.Writer);
    }

    internal static NexNetPipe CreateReader()
    {
        return new NexNetPipe(Mode.Reader);
    }

    internal void Reset()
    {
        _pipe.Reset();
    }
    internal void Configure(int invocationId, INexNetSession nexNetSession)
    {
        _invocationId = invocationId;
    }

    internal void WriteFromStream(ReadOnlySequence<byte> data)
    {
        if (_mode != Mode.Reader)
            throw new InvalidOperationException("Can't write to a non-reading pipe.");

        data.CopyTo(_pipe.Writer.GetSpan((int)data.Length));
    }
    /*
    private class PipeReaderImpl : PipeReader
    {
        private readonly NexNetPipe _pipe;
        public readonly ResetAwaiterSource ReceivedAwaiter = new ResetAwaiterSource(true);
        private bool _complete;
        private bool _cancel;
        public PipeReaderImpl(NexNetPipe pipe)
        {
            _pipe = pipe;
        }

        public void Reset()
        {
            _complete = false;
            _cancel = false;

            ReceivedAwaiter.Reset();
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
            
            _pipe.BufferWriter.GetSequence()
            throw new NotImplementedException();
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            throw new NotImplementedException();

            
        }

        public override void CancelPendingRead()
        {
            _cancel = true;
            ReceivedAwaiter.TrySetCanceled();
        }

        public override void Complete(Exception? exception = null)
        {
            _complete = true;
            ReceivedAwaiter.TrySetResult();
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            if (_pipe.BufferWriter.Length == 0)
                await ReceivedAwaiter.Awaiter;

            if (_pipe.BufferWriter.Length == 0)
                return new ReadResult(new ReadOnlySequence<byte>(), _cancel, _complete);

            return new ReadResult(_pipe.BufferWriter.GetBuffer(), _cancel, _complete);
        }

        public override bool TryRead([UnscopedRef] out ReadResult result)
        {
            if (_pipe.BufferWriter.Length == 0)
            {
                result = new ReadResult(new ReadOnlySequence<byte>(), _cancel, _complete);
                return false;
            }

            result = new ReadResult(_pipe.BufferWriter.GetBuffer(), _cancel, _complete);

            return true;
        }
    }*/

}
