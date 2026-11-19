using System.Collections.Concurrent;

namespace NexNet.Interop.Remote;

/// <summary>
/// Test scopes of language hosts (control protocol §5): opens the scope of the running TUnit test on a host, and at the
/// end of the test closes it (<c>test.end</c>), fails the test when the host reports a leak and attaches the control
/// transcript when the test failed.
/// </summary>
/// <remarks>
/// The end-of-test work runs from the test's <c>OnTestEnd</c> event, which <see cref="BeginAsync"/> subscribes once per
/// test and host; it is the <c>[After(Test)]</c> audit of every test that creates a remote client, without a hook in the
/// test class. Outside a TUnit test, scopes are named "adhoc" and closed with <see cref="EndAsync"/>.
/// </remarks>
public static class RemoteTestAudit
{
    /// <summary>The scope of code that runs outside a TUnit test.</summary>
    public const string AdhocTest = "adhoc";

    private static readonly ConcurrentDictionary<(ControlConnection, string), byte> Hooked = new();

    /// <summary>The test id of the running TUnit test, or <see cref="AdhocTest"/>.</summary>
    public static string CurrentTestId => TestContext.Current?.Id ?? AdhocTest;

    /// <summary>Opens the scope of the running test on <paramref name="connection"/> and hooks the end of the test.</summary>
    /// <returns>The test id.</returns>
    public static async Task<string> BeginAsync(ControlConnection connection, string language)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var context = TestContext.Current;
        var test = context?.Id ?? AdhocTest;
        await connection.BeginTestAsync(test).ConfigureAwait(false);

        if (context?.Events is TestContextEvents events && Hooked.TryAdd((connection, test), 0))
        {
            events.OnTestEnd += async (_, ended) =>
            {
                Hooked.TryRemove((connection, test), out byte _);
                await EndAsync(connection, language, test, ended).ConfigureAwait(false);
            };
        }

        return test;
    }

    /// <summary>
    /// Closes the scope <paramref name="test"/>: sends <c>test.end</c>, attaches the transcript to
    /// <paramref name="context"/> when the test failed or leaked, and throws <see cref="LeakDetectedException"/> on a leak.
    /// </summary>
    /// <returns>The leak report; null when the scope was not open.</returns>
    public static async Task<LeakReport?> EndAsync(ControlConnection connection, string language, string test, TestContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(test);
        LeakReport? report = null;
        Exception? failure = null;
        try
        {
            report = await connection.EndTestAsync(test).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HostCrashedException or ObjectDisposedException)
        {
            // The host crashed: the test failed already, and its scope is gone with the host.
        }
        catch (Exception e)
        {
            failure = e;
        }

        var leaked = report is { IsClean: false };
        var lines = connection.Transcript.Remove(test);
        if (context != null && (leaked || failure != null || HasFailed(context)))
            AttachTranscript(context, language, lines);

        if (failure != null)
            throw new InvalidOperationException($"Closing the test scope on the {language} host failed: {failure.Message}", failure);
        if (leaked)
            throw new LeakDetectedException(language, report!);
        return report;
    }

    private static bool HasFailed(TestContext context)
        => context.Execution.Result?.State is TestState.Failed or TestState.Timeout;

    private static void AttachTranscript(TestContext context, string language, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return;

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "nexnet-interop-transcripts");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, language + "-" + Sanitize(context.Id) + ".log");
            File.WriteAllLines(path, lines);
            context.Output.AttachArtifact(path, "control transcript (" + language + ")", "The last control messages of the test on the " + language + " host.");
        }
        catch (Exception)
        {
            // An artifact is a diagnostic; never fail the test over it.
        }
    }

    private static string Sanitize(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(text.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
