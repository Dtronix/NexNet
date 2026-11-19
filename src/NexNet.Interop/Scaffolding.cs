using NexNet.Interop;

// The C# test scaffolding (H06) of the shared test nexuses: Remote_<IServerNexus> remote proxies, <ClientNexus>Script
// facades and the typed-JSON codecs of every type they use. Generated here once, so test projects referencing
// NexNet.Interop share them. Add a pair when a test needs another nexus.
[assembly: NexusTestScaffolding<NexNet.TestNexus.Interop.IInteropServerNexus, NexNet.TestNexus.Interop.IInteropClientNexus>]
[assembly: NexusTestScaffolding<NexNet.IntegrationTests.TestInterfaces.IServerNexus, NexNet.IntegrationTests.TestInterfaces.IClientNexus>]
