using System.Runtime.CompilerServices;
using System.Text;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Session;

namespace NexNet.Inspector.Tests.Text;

/// <summary>
/// The checked-in golden outputs (Golden/Text/): the text renderer's output for every session of a fixture at one
/// level. Set NEXNET_INSPECTOR_UPDATE_GOLDEN=1 to rewrite them from the current renderer, then review the diff.
/// </summary>
internal static class TextGolden
{
    public const string UpdateVariable = "NEXNET_INSPECTOR_UPDATE_GOLDEN";

    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    /// <summary>The golden directory in the source tree (goldens are compared in place, not copied to the output).</summary>
    public static string Directory([CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!, "Golden", "Text");

    public static string PathOf(FixtureKind kind, string suffix)
        => Path.Combine(Directory(), Path.GetFileNameWithoutExtension(Sample.Fixtures.FileName(kind)) + "." + suffix + ".txt");

    /// <summary>Every session of a fixture, indexed, in file order.</summary>
    public static (RecordingFile Recording, List<CaptureSession> Sessions) Open(FixtureKind kind)
    {
        var recording = RecordingFile.Open(SessionFixtures.Path(kind));
        var sessions = new List<CaptureSession>();
        foreach (var open in recording.Sessions)
        {
            var session = recording.CreateSession(open.SessionId);
            recording.Index(session);
            sessions.Add(session);
        }

        return (recording, sessions);
    }

    /// <summary>Renders every session of a fixture, a blank line between sessions.</summary>
    public static string Render(FixtureKind kind, TextOptions options)
    {
        var (recording, sessions) = Open(kind);
        using (recording)
        {
            var text = new StringBuilder();
            foreach (var session in sessions)
            {
                if (text.Length > 0)
                    text.Append('\n');
                text.Append(TextRenderer.RenderToString(session, options));
            }

            return text.ToString();
        }
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
