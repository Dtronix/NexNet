namespace NexNet.Inspector.Core.Format;

/// <summary>Which side of a connection the recorded session belongs to.</summary>
public enum NntlSessionRole : byte
{
    /// <summary>A server-side session.</summary>
    Server = 0,

    /// <summary>A client connection.</summary>
    Client = 1,
}

/// <summary>Why a session stopped being recorded.</summary>
public enum NntlCloseKind : byte
{
    /// <summary>The session disconnected; <see cref="NntlSessionClose.DisconnectReason"/> says why.</summary>
    Disconnected = 0,

    /// <summary>Recording stopped while the session stayed connected.</summary>
    RecordingStopped = 1,
}

/// <summary>Flags carried by a transport op block.</summary>
[Flags]
public enum NntlTransportOpFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Some byte ranges were replaced before recording; <see cref="NntlTransportOp.Redactions"/> lists them.</summary>
    Redacted = 1,
}

/// <summary>Kinds of section in a snapshot block. Readers skip kinds they do not know.</summary>
public enum NntlSnapshotSectionKind : byte
{
    /// <summary>Open duplex pipes and channels.</summary>
    OpenPipes = 1,

    /// <summary>Invocations waiting for a result.</summary>
    PendingInvocations = 2,

    /// <summary>Synchronized collection state.</summary>
    CollectionState = 3,
}

/// <summary>Kinds of damage the reader found while scanning a file.</summary>
public enum NntlDamageKind : byte
{
    /// <summary>Bytes between two readable blocks that do not form a valid block (bad checksum, garbled header).</summary>
    Corrupt = 0,

    /// <summary>A block whose checksum matches but whose body cannot be decoded.</summary>
    Malformed = 1,

    /// <summary>The file ends inside a block, typically a crash mid-write; everything before it was recovered.</summary>
    TornTail = 2,
}

/// <summary>Where a reader's index came from.</summary>
public enum NntlIndexSource : byte
{
    /// <summary>The index footer the writer wrote on close.</summary>
    Footer = 0,

    /// <summary>A forward scan of the blocks, because the footer was missing or invalid, or a scan was requested.</summary>
    Scan = 1,
}

/// <summary>How <see cref="NntlReader.Open"/> builds the index.</summary>
public enum NntlOpenMode : byte
{
    /// <summary>Use the footer when it is valid; scan otherwise.</summary>
    PreferFooter = 0,

    /// <summary>Always scan every block, verifying each checksum.</summary>
    Scan = 1,
}
