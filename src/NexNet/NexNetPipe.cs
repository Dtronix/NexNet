using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet;

public class NexNetDuplexBinaryPipe
{

    private class BinaryPipeReader : PipeReader
    {
        public override void AdvanceTo(SequencePosition consumed)
        {
            new Pipe().Reset();
            throw new NotImplementedException();
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            throw new NotImplementedException();
        }

        public override void CancelPendingRead()
        {
            throw new NotImplementedException();
        }

        public override void Complete(Exception? exception = null)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            throw new NotImplementedException();
        }

        public override bool TryRead([UnscopedRef] out ReadResult result)
        {
            result = new ReadResult()
            throw new NotImplementedException();
        }
    }
}
