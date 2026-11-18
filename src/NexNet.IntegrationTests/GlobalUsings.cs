global using TUnit.Assertions.Enums;

// Integration tests share ports, sockets and static state (NexusServer's session id counter), so they run one at a
// time until each test isolates its own resources.
[assembly: NotInParallel]
