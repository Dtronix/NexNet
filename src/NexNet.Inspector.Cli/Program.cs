namespace NexNet.Inspector.Cli;

/// <summary>Entry point of <c>nexnet-tap</c>. The commands (list, record, info, repair) arrive in I10.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Out.WriteLine("nexnet-tap: no commands yet.");
        return args.Length == 0 ? 0 : 2;
    }
}
