using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Hex;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Inspect;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Serialization;
using static NexNet.Inspector.App.Tests.Hex.HexCaptureBuilder;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The hex view gallery section (PKG controls/hex-view): the package's five states on hex-view.html's stream — a pipe
/// write split by read #2, a ping and MessageChunk 2/5 of invocation 812 at 1A3F20 — as default (the chunk selected:
/// type cell ringed, body selected, other frames dimmed), hover (inbound ring on a length byte), active (the 6-byte
/// header selected), error (16 redacted bytes) and empty (the hint); then a session start with both directions
/// interleaved (inspect-session-start: read #1 carries the preamble and a ClientGreeting with a 96-byte redacted token).
/// </summary>
public static class HexGallery
{
    private const double StateWidth = 640;
    private const long Base = 0x1A3EE0;
    private const long Chunk = 0x1A3F21;

    public static Control Build()
    {
        StackPanel rows = new() { Spacing = 16 };
        WrapPanel states = new() { ItemSpacing = 16, LineSpacing = 16 };
        FrameKey chunk = new(TapDirection.Inbound, 3);
        states.Children.Add(Captioned("default · frame selected", State(Demo(redacted: false), s => s.Select(Selection.OfFrame(chunk)))));
        states.Children.Add(Captioned("hover · inbound ring", State(Demo(redacted: false), s => s.Select(Selection.OfFrame(chunk)),
            new HexRange(TapDirection.Inbound, Chunk + 2, Chunk + 3))));
        states.Children.Add(Captioned("active · header selected", State(Demo(redacted: false),
            s => s.Select(Selection.OfBytes(TapDirection.Inbound, new StreamRange(Chunk, 6)) with { Frame = chunk }))));
        states.Children.Add(Captioned("error · redacted", State(Demo(redacted: true), s => s.Select(Selection.OfFrame(chunk)))));
        states.Children.Add(Captioned("empty", State(null, _ => { }, height: 72)));
        rows.Children.Add(states);

        WrapPanel headers = new() { ItemSpacing = 24, LineSpacing = 8 };
        headers.Children.Add(Captioned("pane header · both directions", Header(inbound: true, outbound: true)));
        headers.Children.Add(Captioned("pane header · ▲ OUT off", Header(inbound: true, outbound: false)));
        rows.Children.Add(headers);

        rows.Children.Add(Captioned("session start · ▼ IN and ▲ OUT interleaved, read #1 129 B with 96 redacted cells",
            State(SessionStart(), s => s.Select(Selection.OfBytes(TapDirection.Inbound, new StreamRange(8, 1)) with
            {
                Frame = new FrameKey(TapDirection.Inbound, 1),
            }), height: 392, width: 1392)));
        return rows;
    }

    /// <summary>hex-view.html's stream: read #4470 ends inside a pipe write that read #4471 finishes.</summary>
    internal static CaptureSession Demo(bool redacted)
    {
        byte[] pipe = PipeWrite(0x0103, Pattern(51, 0xA5));
        byte[] first = Join(Preamble(), pipe[..40]);
        byte[] second = Join(pipe[40..], Ping(), HexCaptureBuilder.Chunk(0x032C, 0, Pattern(64, 0xFE)));
        HexCaptureBuilder builder = new(inboundStart: Base);
        builder.Op(TapDirection.Inbound, 1262.1, first);
        if (redacted)
            builder.Op(TapDirection.Inbound, 1262.4, second, new NntlByteRange(0x30, 16));
        else
            builder.Op(TapDirection.Inbound, 1262.4, second);
        builder.Op(TapDirection.Inbound, 1263.6, PipeWrite(0x0103, Pattern(20, 0x40)));
        return builder.Session;
    }

    /// <summary>inspect-session-start's first reads and writes.</summary>
    internal static CaptureSession SessionStart()
    {
        byte[] greeting = ClientGreeting(96, out int token);
        System.Buffers.ArrayBufferWriter<byte> buffer = new();
        MsgPackWriter writer = new(buffer);
        writer.WriteArrayHeader(5);
        writer.Write(1_628_353_112);
        writer.Write(0x3A21L);
        writer.Write(1_048_576);
        writer.Write(32);
        writer.Write(30_000u);
        writer.Flush();
        byte[] server = Frame(NexNet.Messages.MessageType.ServerGreeting, buffer.WrittenSpan);

        return new HexCaptureBuilder()
            .Op(TapDirection.Inbound, 0.214, Join(Preamble(), greeting), new NntlByteRange(8 + token, 96))
            .Op(TapDirection.Outbound, 0.251, Preamble())
            .Op(TapDirection.Outbound, 3.104, server)
            .Op(TapDirection.Inbound, 4.020, Ping())
            .Session;
    }

    private static Control State(CaptureSession? session, Action<SyncBus> setup, HexRange? hover = null, double height = 300,
        double width = StateWidth)
    {
        SyncBus sync = new(AvaloniaUiDispatcher.Instance);
        HexLayout layout = new();
        if (session is not null)
        {
            lock (session.Gate)
                layout.Update(session, HexDirections.Both);
        }

        setup(sync);
        HexView view = new()
        {
            Layout = layout,
            Sync = sync,
            Width = width,
            Height = height,
            HoveredBytes = hover,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Border frame = new() { BorderThickness = new Thickness(1), Child = view, HorizontalAlignment = HorizontalAlignment.Left };
        ControlGallery.Bind(frame, Border.BorderBrushProperty, "Brush.Border.Default");
        return frame;
    }

    private static Control Header(bool inbound, bool outbound)
    {
        HexViewModel model = new(new SyncBus(AvaloniaUiDispatcher.Instance)) { ShowsInbound = inbound, ShowsOutbound = outbound };
        Border bar = new()
        {
            Padding = new Thickness(10, 6),
            Child = new HexPaneHeader { DataContext = model },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ControlGallery.Bind(bar, Border.BackgroundProperty, "Brush.Surface.Panel");
        return bar;
    }

    private static Control Captioned(string caption, Control content)
    {
        StackPanel cell = new() { Spacing = 6 };
        cell.Children.Add(content);
        cell.Children.Add(ControlGallery.Text(caption, "FontSize.Data", "Brush.Text.Secondary"));
        return cell;
    }
}
