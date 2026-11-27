namespace NexNet.Inspector.Core.Text;

/// <summary>How much a text rendering says: one line per session aspect, per message, per frame, or per frame with bytes.</summary>
public enum TextLevel : byte
{
    /// <summary>A few lines per session: header, handshake, per-method counts and latencies, pipes, gaps, end.</summary>
    Summary = 0,

    /// <summary>The default: one line per invocation, channel item run, collection message, pipe event, gap.</summary>
    Messages = 1,

    /// <summary>One line per frame (pings, credits and window updates still collapse).</summary>
    Frames = 2,

    /// <summary>Frames, each followed by a line of its first bytes in hex.</summary>
    Bytes = 3,
}

/// <summary>How rendered lines are written.</summary>
public enum TextFormat : byte
{
    /// <summary>Token-condensed text lines (docs/internals/inspector-text-format.md).</summary>
    Text = 0,

    /// <summary>One JSON object per line with short keys (same document).</summary>
    Ndjson = 1,
}

/// <summary>Directions a rendering keeps, seen from the tapped endpoint.</summary>
[Flags]
public enum TextDirections : byte
{
    /// <summary>Nothing directional (header, gaps and end lines only).</summary>
    None = 0,

    /// <summary>What the tapped endpoint read (`&lt;`).</summary>
    Inbound = 1 << 0,

    /// <summary>What the tapped endpoint wrote (`&gt;`).</summary>
    Outbound = 1 << 1,

    /// <summary>Both.</summary>
    Both = Inbound | Outbound,
}

/// <summary>What a <see cref="TextRecord"/> says; each kind has one text form and one ndjson `k` code.</summary>
public enum TextRecordKind : byte
{
    /// <summary>`@0x1BCB server tcp …`: the session header (ndjson `hdr`).</summary>
    Header = 0,

    /// <summary>`hs ok v1 token=…`: the handshake (`hs`).</summary>
    Handshake = 1,

    /// <summary>`&gt;#5 SendAttachment(…) -&gt; ok 15.3ms`: an invocation (`call`).</summary>
    Call = 2,

    /// <summary>`&lt;#5 SendAttachment -&gt; ok 15.3ms`: a result shown apart from its call (`res`).</summary>
    Result = 3,

    /// <summary>`&gt;#5 cancel`: a cancellation of a call shown before its outcome was known (`cancel`).</summary>
    Cancel = 4,

    /// <summary>`&lt;≋4·4 LogLine ×12 …`: channel items (`item`).</summary>
    Item = 5,

    /// <summary>`&lt;☰1·1 Users Insert v7 …`: collection messages (`coll`).</summary>
    Collection = 6,

    /// <summary>`&gt;⇄3·3 data 64.0KB`: raw pipe data (`data`).</summary>
    Data = 7,

    /// <summary>`&lt;≋4·4 open LogLine by #9`: a pipe opening, refusal or completion (`pipe`).</summary>
    Pipe = 8,

    /// <summary>`ping ×4 credit ×2 (span 1.6s)`: pings, credits and window updates (`noise`).</summary>
    Noise = 9,

    /// <summary>`GAP in 812 ops 1.2MB (Drop)`: ops the capture dropped (`gap`).</summary>
    Gap = 10,

    /// <summary>`END Graceful from client`: how the session ended (`end`).</summary>
    End = 11,

    /// <summary>`&gt;f18 Invocation #5 SendMessage 92B T`: one frame, frames and bytes levels (`frame`).</summary>
    Frame = 12,

    /// <summary>`  0a0b…`: a frame's first bytes, bytes level (`hex`).</summary>
    Bytes = 13,

    /// <summary>A summary-level line (`stat`).</summary>
    Stat = 14,

    /// <summary>`! f12 malformed 40B`: a frame that could not be framed or completed (`prob`).</summary>
    Problem = 15,
}
