namespace NexNet.Inspector.Cli;

internal sealed partial class CliArguments
{
    /// <summary><c>tail --findings</c>: also write the findings detectors' <c>!</c> lines as they are detected.</summary>
    public bool TailFindings => Has(CliOption.Findings);
}
