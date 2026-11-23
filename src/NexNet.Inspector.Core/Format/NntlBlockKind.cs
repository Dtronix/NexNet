namespace NexNet.Inspector.Core.Format;

/// <summary>The kind byte of a <c>.nntl</c> block. Values are part of the file format and never change.</summary>
public enum NntlBlockKind : byte
{
    /// <summary>File header: magic, format version, clock anchor and producer. Always the first block.</summary>
    Header = 1,

    /// <summary>A session starts being recorded.</summary>
    SessionOpen = 2,

    /// <summary>An embedded <c>.nnschema</c> (opaque bytes) and its hash.</summary>
    Schema = 3,

    /// <summary>One raw transport read or write of a session.</summary>
    TransportOp = 4,

    /// <summary>Transport ops of a session that were dropped (Drop overflow mode).</summary>
    Gap = 5,

    /// <summary>Session state captured when recording or attaching began.</summary>
    Snapshot = 6,

    /// <summary>A session stops being recorded.</summary>
    SessionClose = 7,

    /// <summary>Index footer. Written once when the writer closes; the trailer points to it.</summary>
    Index = 8,
}
