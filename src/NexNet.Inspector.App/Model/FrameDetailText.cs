using System.Globalization;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;

namespace NexNet.Inspector.App.Model;

/// <summary>
/// The words of a <see cref="FrameDetail"/> (the Events Target column, a ladder row's label): "NnP␔ · v1",
/// "auth token · redacted", "clientId 0x3A21", "+1", "Ready", "Completed", "Arg · 49,152 B". Formatted only for rows in
/// view; nothing is kept per frame.
/// </summary>
public static class FrameDetailText
{
    /// <summary>The preamble's magic as text: N n P and the DC4 control picture.</summary>
    public const string PreambleMagic = "NnP\u2414";

    /// <summary>The detail's words; null when it adds nothing.</summary>
    /// <param name="detail">The detail.</param>
    /// <param name="flags">The frame's flags: a redacted greeting says so instead of its token's length.</param>
    public static string? Words(in FrameDetail detail, FrameFlags flags) => detail.Kind switch
    {
        FrameDetailKind.Preamble => PreambleMagic + " · v" + detail.Value.ToString(CultureInfo.InvariantCulture),
        FrameDetailKind.AuthToken => (flags & FrameFlags.Redacted) != 0 ? "auth token · redacted"
            : detail.Value < 0 ? "no auth token"
            : "auth token · " + detail.Value.ToString("N0", CultureInfo.InvariantCulture) + " B",
        FrameDetailKind.ClientId => "clientId 0x" + ((ushort)detail.Value).ToString("X4", CultureInfo.InvariantCulture),
        FrameDetailKind.Credit => "+" + detail.Value.ToString(CultureInfo.InvariantCulture),
        FrameDetailKind.PipeState => PipeState((PipeStateFlags)detail.Value),
        FrameDetailKind.Result => Result((InvocationResultState)detail.Value),
        FrameDetailKind.MessageStart => ((LargeMessageKind)detail.Value == LargeMessageKind.Result ? "Result" : "Arg")
            + (detail.Extra > 0 ? " · " + detail.Extra.ToString("N0", CultureInfo.InvariantCulture) + " B" : ""),
        _ => null,
    };

    /// <summary>A pipe state as one word: Ready, Refused, Complete, or which half completed.</summary>
    public static string PipeState(PipeStateFlags state)
    {
        if ((state & PipeStateFlags.Refused) != 0) return "Refused";
        if ((state & PipeStateFlags.Complete) == PipeStateFlags.Complete) return "Complete";
        if ((state & PipeStateFlags.ClientWriterServerReaderComplete) != 0) return "client done";
        if ((state & PipeStateFlags.ClientReaderServerWriterComplete) != 0) return "server done";
        return (state & PipeStateFlags.Ready) != 0 ? "Ready" : "None";
    }

    /// <summary>An InvocationResult's state: Completed, Exception, Unauthorized, Canceled.</summary>
    public static string? Result(InvocationResultState state) => state switch
    {
        InvocationResultState.CompletedResult => "Completed",
        InvocationResultState.Exception => "Exception",
        InvocationResultState.Unauthorized => "Unauthorized",
        InvocationResultState.Canceled => "Canceled",
        _ => null,
    };

    /// <summary>"target name · detail", leaving out the empty parts.</summary>
    public static string Join(string target, string? name, string? detail)
    {
        if (name is not null) target = target.Length == 0 ? name : target + " " + name;
        if (detail is null) return target;
        return target.Length == 0 ? detail : target + " · " + detail;
    }
}
