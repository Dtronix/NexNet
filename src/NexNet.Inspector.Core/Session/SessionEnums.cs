namespace NexNet.Inspector.Core.Session;

/// <summary>Where a capture session's data comes from.</summary>
public enum CaptureSource : byte
{
    /// <summary>A live attach stream from an inspector endpoint.</summary>
    Live = 0,

    /// <summary>A recording on disk.</summary>
    File = 1,
}

/// <summary>Which schema the user chose to decode a session with (the open-recording dialog's choice).</summary>
public enum SchemaChoice : byte
{
    /// <summary>The schema the recording or the endpoint provided; the workspace schema when there is none.</summary>
    Embedded = 0,

    /// <summary>The workspace schema, even when it differs from the embedded one.</summary>
    Workspace = 1,

    /// <summary>No schema: every body decodes raw.</summary>
    Raw = 2,
}

/// <summary>Which schema actually decodes a session.</summary>
public enum SchemaSource : byte
{
    /// <summary>None: bodies decode raw.</summary>
    None = 0,

    /// <summary>The embedded (or endpoint-provided) schema.</summary>
    Embedded = 1,

    /// <summary>The workspace schema.</summary>
    Workspace = 2,
}

/// <summary>What is known about a session's embedded schema, compared with the workspace schema.</summary>
public enum SchemaStatus : byte
{
    /// <summary>The session names no schema.</summary>
    Missing = 0,

    /// <summary>The embedded schema was read; it matches the workspace schema, or there is none to compare with.</summary>
    Embedded = 1,

    /// <summary>The embedded schema differs from the workspace schema (the open-recording mismatch banner).</summary>
    Mismatch = 2,

    /// <summary>The session names a schema that is not in the stream or does not read.</summary>
    Unreadable = 3,
}

/// <summary>Problems with a recording the viewer warns about (a banner); the readable blocks still open.</summary>
[Flags]
public enum RecordingWarnings : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The index footer was missing or invalid, so the file was scanned: typically a crashed recording.</summary>
    IndexFromScan = 1 << 0,

    /// <summary>Some regions of the file could not be read (see the damage list).</summary>
    Damaged = 1 << 1,
}

/// <summary>Where a capture session's background indexing stands.</summary>
public enum IndexingState : byte
{
    /// <summary>Not started.</summary>
    NotStarted = 0,

    /// <summary>Blocks are being read and parsed.</summary>
    Indexing = 1,

    /// <summary>Every block was read.</summary>
    Complete = 2,

    /// <summary>Indexing was canceled (the document closed).</summary>
    Canceled = 3,

    /// <summary>Indexing stopped on an error.</summary>
    Failed = 4,
}

/// <summary>Where a capture session stands in its life.</summary>
public enum CaptureState : byte
{
    /// <summary>Data is still arriving (a live attach) or being indexed (a file).</summary>
    Open = 0,

    /// <summary>The session disconnected; its close block was seen.</summary>
    Ended = 1,

    /// <summary>The capture stopped while the session stayed connected (detach, or recording stopped).</summary>
    Stopped = 2,

    /// <summary>The stream or recording ended without a close block (a crash, a torn file).</summary>
    Truncated = 3,
}
