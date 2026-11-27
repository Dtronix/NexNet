namespace NexNet.Inspector.Core.Text;

/// <summary>
/// The short legend of the text format that `nexnet-tap help format` prints (at most 20 lines). The full spec is
/// docs/internals/inspector-text-format.md.
/// </summary>
public static class TextLegend
{
    /// <summary>The legend, lines separated by '\n', ending with one.</summary>
    public const string Text =
        "@0x1BCB server tcp <remote> schema Chat#b144aae0 t0=<utc>   session header (id hex, role, transport)\n" +
        "+22.0          ms since t0; > sent by the tapped endpoint, < received\n" +
        "#5  #5.2       invocation by wire id (.2 = 2nd use of the id); f18 = 18th frame; @0x.. = session\n" +
        ">#5 M(args) -> ok <value> 15.3ms     call; -> exception|canceled|unauthorized|refused|… (pending); (oneway)\n" +
        "<#5 M -> ok    result of a call shown earlier as pending\n" +
        "≋4·4 LogLine ×12 <first> (…11 more)  channel items on pipe client·server 4·4\n" +
        "☰1·1 Users Insert v7 @3 <value>      collection message (version, index); ⇄3·3 data 64.0KB = raw pipe\n" +
        "≋4·4 open LogLine by #9 | complete | client done | refused      pipe events\n" +
        "hs ok v1 token=redacted(sha256:…) id=7       handshake: protocol version, token as recorded, client id\n" +
        "ping ×4 credit ×2 window ×6 (span 1.6s)      pings, credits, window updates collapsed\n" +
        "×N (span 1.2s)  repeated identical lines; #5..#9 = first and last call collapsed\n" +
        "GAP in 812 ops 1.2MB (Drop)   the capture dropped ops; END <reason> from <side>\n" +
        "! f12 malformed 40B    a frame that could not be framed, completed or decoded\n" +
        "! warn slow #21 Work took 251.2ms; …   finding: error|warn|info, kind, the IDs it cites, message\n" +
        "\"text…\" {…} [n] …+3   values cut by --value-width and --depth\n" +
        "frames: >f18 Invocation #5 SendMessage 92B TNX…R   flags: T transport split, N NexNet split,\n" +
        "        X cancel, … incomplete, R redacted, ! body malformed; bytes adds the frame's first bytes in hex\n" +
        "… budget hit, N withheld, --after <cursor>    resume with --after; tokens ≈ chars/4 (non-ASCII ×3)\n" +
        "Levels: summary | messages (default) | frames | bytes.  Formats: text | ndjson (same records, short keys)\n";
}
