using NexNet.Logging;
using NexNet.Pipes;

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// Pipe readers and writers for tests that drive them directly, without a session granting credit.
/// </summary>
internal static class TestPipes
{
    /// <summary>
    /// Window large enough that tests not about flow control never run out of credit.
    /// </summary>
    public const int LargeWindow = 1 << 30;

    /// <summary>
    /// A reader with its window open, as after the open handshake.
    /// </summary>
    public static NexusPipeReader Reader(IPipeStateManager stateManager, INexusLogger? logger = null, bool isServer = true, int window = LargeWindow)
    {
        var reader = new NexusPipeReader(stateManager, logger, isServer, window);
        reader.OpenWindow();
        return reader;
    }

    /// <summary>
    /// Gives a writer credit to send, as the peer's window grant would.
    /// </summary>
    public static NexusPipeWriter WithCredit(this NexusPipeWriter writer, long credit = LargeWindow)
    {
        writer.AddCredit(credit);
        return writer;
    }
}
