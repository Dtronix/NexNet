namespace NexNet.Inspector.Cli;

/// <summary>The verb of a <c>nexnet-tap</c> command line.</summary>
internal enum CliCommand : byte
{
    None = 0,
    List = 1,
    Record = 2,
    Info = 3,
    Repair = 4,
    Help = 5,
    Version = 6,
    Tail = 7,
    Summary = 8,
    Show = 9,
    Trace = 10,
    Around = 11,
    Errors = 12,
    Slow = 13,
    Pipes = 14,
    Collections = 15,
    Schema = 16,
    Diff = 17,
    Export = 18,
    Format = 19,
    Findings = 20,
}

/// <summary>Named options. A flag set, so each command can declare which ones it accepts and which it requires.</summary>
[Flags]
internal enum CliOption : ulong
{
    None = 0,
    Endpoint = 1 << 0,
    Token = 1 << 1,
    Insecure = 1 << 2,
    ServerName = 1 << 3,
    ConnectTimeout = 1 << 4,
    Session = 1 << 5,
    Duration = 1 << 6,
    Out = 1 << 7,
    Overflow = 1 << 8,
    IncludeSecrets = 1 << 9,
    NoSnapshot = 1 << 10,
    Wait = 1 << 11,
    Force = 1 << 12,
    Json = 1 << 13,
    Messages = 1 << 14,
    Help = 1 << 15,
    Level = 1UL << 16,
    Format = 1UL << 17,
    Collapse = 1UL << 18,
    ValueWidth = 1UL << 19,
    Depth = 1UL << 20,
    MaxLines = 1UL << 21,
    MaxTokens = 1UL << 22,
    After = 1UL << 23,
    Method = 1UL << 24,
    Pipe = 1UL << 25,
    Dir = 1UL << 26,
    Only = 1UL << 27,
    Slow = 1UL << 28,
    Grep = 1UL << 29,
    Sample = 1UL << 30,
    Until = 1UL << 31,
    Context = 1UL << 32,
    Follow = 1UL << 33,
    Bytes = 1UL << 34,
    Over = 1UL << 35,
    Contents = 1UL << 36,
    From = 1UL << 37,
    To = 1UL << 38,
    Top = 1UL << 39,
    Findings = 1UL << 40,
}

/// <summary>What <c>record --session</c> names.</summary>
internal enum SessionSelectorKind : byte
{
    /// <summary>One session by its ID (or, on a client, its connected session ID).</summary>
    Id = 0,

    /// <summary>The first session the endpoint lists; waits for one when there is none yet.</summary>
    First = 1,

    /// <summary>Every session: the current ones from now and every new one from its first byte.</summary>
    All = 2,
}

/// <summary>How an inspector endpoint address reaches the endpoint.</summary>
internal enum EndpointScheme : byte
{
    Tcp = 0,
    Tls = 1,
    Uds = 2,
}

/// <summary>Process exit codes of <c>nexnet-tap</c>. Listed in the usage text; scripts depend on the values.</summary>
internal enum ExitCode
{
    /// <summary>Success. <c>record</c> also returns it when stopped by Ctrl+C or <c>--duration</c>.</summary>
    Ok = 0,

    /// <summary>A runtime failure: the endpoint is unreachable or refused the token, an I/O error, an unreadable file.</summary>
    Failed = 1,

    /// <summary>The command line is invalid.</summary>
    Usage = 2,

    /// <summary>The server's configuration refused the request (recording disabled, secrets not included).</summary>
    Refused = 3,

    /// <summary>The requested session does not exist, or none appeared within <c>--wait</c>.</summary>
    NotFound = 4,

    /// <summary><c>record</c> was interrupted (Ctrl+C) before recording started.</summary>
    Cancelled = 130,
}
