namespace NexNet.Inspector.Cli;

/// <summary>Entry point of <c>nexnet-tap</c>; the commands live in <see cref="TapCli"/>.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var interrupt = new CancellationTokenSource();
        // The first Ctrl+C stops a recording cleanly (detach, then the footer arrives); a second one ends the process.
        Console.CancelKeyPress += (_, e) =>
        {
            if (interrupt.IsCancellationRequested)
                return;
            e.Cancel = true;
            interrupt.Cancel();
        };

        try
        {
            // tail prints the text format's glyphs (≋ ☰ ⇄ · × …); the Windows console default code page would turn them into '?'.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console to configure.
        }

        // mcp reads the client's messages from stdin as UTF-8, whatever the console's input code page.
        var cli = new TapCli(Console.Out, Console.Error)
        {
            Input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false)),
        };
        return await cli.RunAsync(args, Environment.GetEnvironmentVariable(CliArguments.TokenVariable), interrupt.Token)
            .ConfigureAwait(false);
    }
}
