using System.IO.Pipelines;
using NexNet.Transports;

namespace NexNet.Internals;

/// <summary>
/// What a session reads and writes through, next to its transport: tests check that an untapped session uses the
/// transport's own reader and writer.
/// </summary>
internal interface ISessionTransportView
{
    /// <summary>The reader the session reads through; null once it has disconnected.</summary>
    PipeReader? Input { get; }

    /// <summary>The writer the session writes through; null once it has disconnected.</summary>
    PipeWriter? Output { get; }

    /// <summary>The session's transport.</summary>
    ITransport Transport { get; }
}
