namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>How the ladder spaces its rows (OQ6).</summary>
public enum LadderMode : byte
{
    /// <summary>Every row 18 px, one after another (the default).</summary>
    Compact = 0,

    /// <summary>A gap above each row grows with the time since the row before it (log scale, capped).</summary>
    TimeScaled = 1,
}

/// <summary>What a ladder row's target label names.</summary>
public enum LadderTarget : byte
{
    /// <summary>Nothing (ping, disconnects, greetings, the preamble).</summary>
    None = 0,

    /// <summary>An invocation: "inv 812".</summary>
    Invocation = 1,

    /// <summary>A raw duplex pipe: "pipe 3·1".</summary>
    Pipe = 2,

    /// <summary>A pipe carrying a typed channel: "chan 4·2", with the CH chip.</summary>
    Channel = 3,

    /// <summary>A pipe carrying a synchronized collection: "coll 1·1", with the CO chip.</summary>
    Collection = 4,
}

/// <summary>The short note after a row's target ("inv 812 · Completed").</summary>
public enum LadderNote : byte
{
    /// <summary>No note.</summary>
    None = 0,

    /// <summary>A MessageStart of a large argument.</summary>
    Argument = 1,

    /// <summary>A MessageStart of a large result.</summary>
    Result = 2,

    /// <summary>An InvocationResult of a method that completed.</summary>
    Completed = 3,

    /// <summary>An InvocationResult of a method that threw.</summary>
    Exception = 4,

    /// <summary>An InvocationResult authorization refused.</summary>
    Unauthorized = 5,

    /// <summary>An InvocationResult of a canceled method.</summary>
    Canceled = 6,

    /// <summary>A frame whose tail has not arrived (or never will).</summary>
    Partial = 7,
}

/// <summary>What a trace follows.</summary>
public enum LadderTraceKind : byte
{
    /// <summary>Nothing is traced: no brackets.</summary>
    None = 0,

    /// <summary>Every frame of one entity (an invocation, pipe or greeting).</summary>
    Entity = 1,

    /// <summary>Every frame of one assembled message (a large message or a pipe run).</summary>
    Message = 2,

    /// <summary>One frame that carries its message whole.</summary>
    Frame = 3,

    /// <summary>The frames of the trace resolved on the sync bus (<see cref="LadderTrace.Members"/>).</summary>
    Members = 4,
}

/// <summary>How a row takes part in the trace bracket.</summary>
public enum LadderBracket : byte
{
    /// <summary>Outside the traced span: no bracket.</summary>
    None = 0,

    /// <summary>A frame of the traced message: the solid 3 px bar.</summary>
    Member = 1,

    /// <summary>A frame inside the span that is not part of it: the 2 px dotted bar, and the row at 60 %.</summary>
    Interleaved = 2,
}
