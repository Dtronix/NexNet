namespace NexNet.Inspector.Core.Assembly;

/// <summary>What a <see cref="FrameDetail"/> says about a frame's body.</summary>
public enum FrameDetailKind : byte
{
    /// <summary>Nothing beyond the frame's target (or the body did not read).</summary>
    None = 0,

    /// <summary>The preamble; <see cref="FrameDetail.Value"/> is the protocol version.</summary>
    Preamble = 1,

    /// <summary>A client greeting; <see cref="FrameDetail.Value"/> is the auth token's length, -1 for none.</summary>
    AuthToken = 2,

    /// <summary>The server greeting; <see cref="FrameDetail.Value"/> is the session ID it assigned the client.</summary>
    ClientId = 3,

    /// <summary>An InvocationCredit; <see cref="FrameDetail.Value"/> is the credits returned.</summary>
    Credit = 4,

    /// <summary>A DuplexPipeUpdateState; <see cref="FrameDetail.Value"/> is its <see cref="PipeStateFlags"/>.</summary>
    PipeState = 5,

    /// <summary>An InvocationResult; <see cref="FrameDetail.Value"/> is its <see cref="InvocationResultState"/>.</summary>
    Result = 6,

    /// <summary>
    /// A MessageStart; <see cref="FrameDetail.Value"/> is its <see cref="LargeMessageKind"/> and
    /// <see cref="FrameDetail.Extra"/> the message's total length.
    /// </summary>
    MessageStart = 7,
}

/// <summary>
/// The few words a one-line view (an Events row's Target, a ladder row's label) says about a frame's body beyond its
/// type and target: the preamble's version, a greeting's token or assigned ID, credits returned, a pipe's new state, a
/// result's state, a large message's kind and size. Numbers only, read from assembly's rows or the body's first bytes;
/// the view formats them when the row is shown.
/// </summary>
/// <param name="Kind">What <paramref name="Value"/> and <paramref name="Extra"/> mean.</param>
/// <param name="Value">The main value.</param>
/// <param name="Extra">A second value, for kinds that have one.</param>
public readonly record struct FrameDetail(FrameDetailKind Kind, long Value, long Extra = 0)
{
    /// <summary>No detail.</summary>
    public static FrameDetail None => default;
}
