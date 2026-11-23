using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Session;

namespace NexNet.Inspector.Tests.Compare;

/// <summary>Indexed sessions of the fixtures, opened once: compares only read them.</summary>
internal static class CompareFixtures
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<FixtureKind, (RecordingFile File, CaptureSession Session)> Opened = [];

    /// <summary>The first session of a fixture, indexed; its file stays open (payloads are read from it).</summary>
    public static CaptureSession Session(FixtureKind kind)
    {
        lock (Gate)
        {
            if (!Opened.TryGetValue(kind, out var entry))
            {
                var file = RecordingFile.Open(SessionFixtures.Path(kind));
                var session = file.CreateSession(file.Sessions[0].SessionId);
                file.Index(session);
                Opened[kind] = entry = (file, session);
            }

            return entry.Session;
        }
    }

    /// <summary>A second, independent copy of a fixture's first session (the same run opened twice).</summary>
    public static CaptureSession Copy(FixtureKind kind, out RecordingFile file)
    {
        file = RecordingFile.Open(SessionFixtures.Path(kind));
        var session = file.CreateSession(file.Sessions[0].SessionId);
        file.Index(session);
        return session;
    }
}
