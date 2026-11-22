namespace NexNet.Interop;

/// <summary>
/// What a client under test still holds at the end of a test (<see cref="IClientUnderTest.EndTestAuditAsync"/>). Any
/// leak fails the test.
/// </summary>
/// <param name="OpenClients">Clients not disposed.</param>
/// <param name="OpenPipes">Pipes neither completed nor disposed.</param>
/// <param name="Timers">Timers still scheduled.</param>
/// <param name="PendingInvocations">Invocations not completed.</param>
/// <param name="Subscriptions">Collections still enabled, event subscriptions still attached.</param>
/// <param name="Details">One line per leak, for the failure message.</param>
public sealed record LeakReport(
    int OpenClients,
    int OpenPipes,
    int Timers,
    int PendingInvocations,
    int Subscriptions,
    IReadOnlyList<string> Details)
{
    /// <summary>No leak.</summary>
    public static LeakReport Clean { get; } = new(0, 0, 0, 0, 0, []);

    /// <summary>True when nothing leaked.</summary>
    public bool IsClean => OpenClients == 0 && OpenPipes == 0 && Timers == 0 && PendingInvocations == 0 && Subscriptions == 0;

    /// <inheritdoc />
    public override string ToString()
        => IsClean
            ? "no leaks"
            : $"clients={OpenClients} pipes={OpenPipes} timers={Timers} invocations={PendingInvocations} subscriptions={Subscriptions}"
              + (Details.Count == 0 ? "" : ": " + string.Join("; ", Details));
}
