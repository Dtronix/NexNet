using NexNet.Interop;

namespace NexNet.IntegrationTests.Interop;

/// <summary>
/// Writes the client matrix coverage report at the end of the test session when <c>NEXNET_MATRIX_REPORT</c> names a
/// directory, and in CI fails the session when a matrix row was skipped for a reason that cites no card
/// (<see cref="MatrixCoverage.WriteRequestedReportAsync"/>).
/// </summary>
internal static class MatrixCoverageHook
{
    [After(HookType.TestSession)]
    public static Task WriteReportAsync() => MatrixCoverage.WriteRequestedReportAsync();
}
